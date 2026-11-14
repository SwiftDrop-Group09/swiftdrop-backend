using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SwiftDrop.Auditing;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Admin;

[Collection("Postgres")]
public sealed class ActivityAuditTests(PostgresFixture fixture)
{
    private async Task<WebApplication> Host(Guid actor, string role, bool trusted = false)
    {
        await using var db = new DeliveryDbContext(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(fixture.ConnectionString).Options);
        await db.Database.MigrateAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:Database"] = fixture.ConnectionString, ["Audit:Enabled"] = "true",
            ["Audit:TrustedProxies:0"] = trusted ? "192.0.2.7" : "192.0.2.8"
        });
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, EmptyAuthentication>("test", _ => { });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.Use(async (context, next) => {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.7");
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new("sub", actor.ToString()), new("roles", role)], "fixture", "sub", "roles"));
            await next(context);
        });
        app.UseActivityAudit("Delivery", "/api/v1/delivery/audit-logs");
        app.UseAuthorization();
        app.MapPost("/api/v1/fixture/{id:guid}", () => Results.Ok(new { saved = true })).WithName("SaveFixture");
        await app.StartAsync();
        return app;
    }

    [Theory]
    [InlineData(false, "192.0.2.7", "Peer")]
    [InlineData(true, "203.0.113.9", "TrustedProxy")]
    public async Task RecordsVerifiedActorAndOnlyTrustedIpWithoutBodiesOrQueryValues(bool trusted, string expectedIp, string origin)
    {
        var actor = Guid.NewGuid(); var resource = Guid.NewGuid();
        await using var app = await Host(actor, "Customer", trusted);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.99,203.0.113.9");
        using var response = await client.PostAsync($"/api/v1/fixture/{resource}?password=never-store-this", new StringContent("private-body-content", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ActivityAudit.Read(app.Configuration, "Delivery", new QueryCollection(QueryStringActor(actor)), default);
        var row = Assert.Single(result.Items);
        Assert.Equal(actor, row.ActorId); Assert.Equal(["Customer"], row.Roles); Assert.Equal("Human", row.ActorType);
        Assert.Equal(expectedIp, row.IpAddress); Assert.Equal(origin, row.IpSource); Assert.Equal(resource, row.ResourceId);
        Assert.Equal("/api/v1/fixture/{id:guid}", row.Route); Assert.Equal("SaveFixture", row.Action); Assert.Equal("Success", row.Outcome);
        var json = System.Text.Json.JsonSerializer.Serialize(row);
        Assert.DoesNotContain("private-body-content", json); Assert.DoesNotContain("never-store-this", json);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM actor_activity_audit WHERE operation_id=@id", connection);
        command.Parameters.AddWithValue("id", row.Id); Assert.Equal(2L, await command.ExecuteScalarAsync());
        command.CommandText = "DELETE FROM actor_activity_audit WHERE operation_id=@id";
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23514", exception.SqlState);
    }

    [Fact]
    public async Task ExclusionsFilterAllMatchingRecordsBeforePagination()
    {
        var actor = Guid.NewGuid(); await using var app = await Host(actor, "Admin");
        using var client = app.GetTestClient();
        using var created = await client.PostAsync($"/api/v1/fixture/{Guid.NewGuid()}", null);
        foreach (var excluded in new[] { "notRole=Admin", "notMethod=POST", "notOutcome=Success", "notActorType=Human", "notKind=Request", "notAction=SaveFixture", "notIp=192.0.2.7" })
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery($"?actorId={actor}&{excluded}");
            var result = await ActivityAudit.Read(app.Configuration, "Delivery", new QueryCollection(query), default);
            Assert.Empty(result.Items); Assert.Equal(0, result.TotalCount);
        }
    }

    [Theory]
    [InlineData("Admin", HttpStatusCode.OK)]
    [InlineData("Customer", HttpStatusCode.Forbidden)]
    [InlineData("Courier", HttpStatusCode.Forbidden)]
    [InlineData("Dispatcher", HttpStatusCode.Forbidden)]
    [InlineData("Merchant", HttpStatusCode.Forbidden)]
    public async Task AuditReadsRequireAdmin(string role, HttpStatusCode expected)
    {
        await using var app = await Host(Guid.NewGuid(), role);
        using var client = app.GetTestClient(); using var response = await client.GetAsync("/api/v1/delivery/audit-logs?limit=20");
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task FiltersRejectInvalidValuesAndDoNotInterpretSearchAsSql()
    {
        var actor = Guid.NewGuid(); await using var app = await Host(actor, "Admin"); using var client = app.GetTestClient();
        foreach (var query in new[] { "ip=not-an-ip", "limit=0", "limit=abc", "actorId=bad", "before=2026-01-01T00:00:00Z", "method=BAD", "unexpected=1", "notIp=bad", "notActorId=bad", "notMethod=BAD", "notRole=a&notRole=b" })
        {
            using var response = await client.GetAsync("/api/v1/delivery/audit-logs?" + query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        var filter = QueryStringActor(actor);
        filter.Add("action", "' OR 1=1 --");
        var page = await ActivityAudit.Read(app.Configuration, "Delivery", new QueryCollection(filter), default);
        Assert.Empty(page.Items);
    }

    private static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> QueryStringActor(Guid actor) => new() { ["actorId"] = actor.ToString(), ["kind"] = "Request" };
    private sealed class EmptyAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    { protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult()); }
}
