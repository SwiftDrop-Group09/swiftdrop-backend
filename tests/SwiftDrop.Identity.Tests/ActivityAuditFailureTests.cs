using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using SwiftDrop.Auditing;
using SwiftDrop.Identity.Tests.Persistence;

namespace SwiftDrop.Identity.Tests;

[Collection(IdentityPostgresCollection.Name)]
public sealed class ActivityAuditFailureTests(IdentityPostgresFixture database)
{
    [Theory]
    [InlineData("GET", "/api/v1/auth/csrf", false)]
    [InlineData("POST", "/api/v1/auth/login", false)]
    [InlineData("POST", "/api/v1/auth/refresh", false)]
    [InlineData("GET", "/api/v1/users/me", true)]
    public async Task StorageFailureIsPrivateAndDoesNotExecuteAuthentication(string method, string path, bool authenticated)
    {
        var probe = new EndpointProbe();
        var logs = new CapturedLogs();
        await using var app = await Host(null, probe, logs, authenticated);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(probe.Executed);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("audit_unavailable", body.RootElement.GetProperty("code").GetString());
        var correlation = body.RootElement.GetProperty("correlationId").GetGuid();
        Assert.NotEqual(Guid.Empty, correlation);
        Assert.Equal(correlation.ToString("D"), Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        Assert.False(body.RootElement.TryGetProperty("csrfToken", out _));
        Assert.False(body.RootElement.TryGetProperty("googleNonce", out _));
        Assert.Contains(logs.Messages, message => message.Contains("configuration_invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingAuditSchemaHasActionableLogsWithoutDatabaseDetailsInTheResponse()
    {
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            SearchPath = "missing_audit_" + Guid.NewGuid().ToString("N")
        };
        var probe = new EndpointProbe();
        var logs = new CapturedLogs();
        await using var app = await Host(connection.ConnectionString, probe, logs);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(probe.Executed);
        Assert.Contains(logs.Messages, message => message.Contains("schema_missing", StringComparison.Ordinal)
            && message.Contains("42P01", StringComparison.Ordinal)
            && message.Contains("pending service database migrations", StringComparison.Ordinal));
        Assert.DoesNotContain("actor_activity_audit", body, StringComparison.Ordinal);
        Assert.DoesNotContain("42P01", body, StringComparison.Ordinal);
        Assert.DoesNotContain(connection.SearchPath, body, StringComparison.Ordinal);
        Assert.All(logs.Messages, message => Assert.DoesNotContain(connection.ConnectionString, message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAnonymousChallengeKeepsItsOriginalStatusWhenAuditStorageIsUnavailable()
    {
        var probe = new EndpointProbe();
        await using var app = await Host(null, probe, new CapturedLogs());
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/v1/denied");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<WebApplication> Host(string? connection, EndpointProbe probe, CapturedLogs logs, bool authenticated = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:Enabled"] = "true",
            ["ConnectionStrings:Database"] = connection
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (authenticated)
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "fixture"));
            await next(context);
        });
        app.UseActivityAudit("Identity", "/api/v1/audit-logs/activity");
        app.UseAuthorization();
        IResult Endpoint()
        {
            probe.Executed = true;
            return Results.Ok(new { csrfToken = "must-not-be-issued", googleNonce = "must-not-be-issued" });
        }
        app.MapGet("/api/v1/auth/csrf", Endpoint);
        app.MapPost("/api/v1/auth/login", Endpoint);
        app.MapPost("/api/v1/auth/refresh", Endpoint);
        app.MapGet("/api/v1/users/me", Endpoint);
        app.MapGet("/api/v1/denied", () => Results.Unauthorized());
        await app.StartAsync();
        return app;
    }

    private sealed class EndpointProbe
    {
        public bool Executed { get; set; }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }
}
