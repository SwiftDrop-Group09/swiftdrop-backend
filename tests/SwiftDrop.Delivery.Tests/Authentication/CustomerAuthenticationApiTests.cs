using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Authentication;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Authentication;

[Collection("Postgres")]
public sealed class CustomerAuthenticationApiTests(PostgresFixture fixture)
{
    private const string MalformedPrivateInput = "{\"recipient\":\"private-authentication-input\"";

    [Theory]
    [InlineData("Development", "/api/v1/quotes")]
    [InlineData("Development", "/api/v1/parcels")]
    [InlineData("Production", "/api/v1/quotes")]
    [InlineData("Production", "/api/v1/parcels")]
    public async Task NormalHostsDoNotRegisterTestIdentityOrAcceptRequestDerivedCredentials(string environment, string route)
    {
        using var factory = new QuoteApiFactory(null, replaceAuthentication: false, environmentName: environment);
        using var configuredFactory = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Development:Enabled"] = "true",
                ["Authentication:Development:CustomerId"] = DeliveryTestAuthentication.CustomerId.ToString("D"),
                ["Delivery:Authentication:UseDevelopmentIdentity"] = "true",
                ["DevelopmentAuthentication:Enabled"] = "true"
            })));
        using var client = configuredFactory.CreateClient();
        AddSpoofedIdentity(client, DeliveryTestAuthentication.CustomerId);
        Assert.Equal(environment, configuredFactory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        Assert.Equal("true", configuredFactory.Services.GetRequiredService<IConfiguration>()["Authentication:Development:Enabled"]);
        var schemes = configuredFactory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.DoesNotContain(await schemes.GetAllSchemesAsync(), scheme => scheme.Name == DeliveryTestAuthentication.SchemeName);
        Assert.Equal(UnconfiguredIdentityHandler.SchemeName, (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);

        using var response = await PostRawAsync(client, route, MalformedPrivateInput, NewKey());

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Bearer");
    }

    [Theory]
    [InlineData("/api/v1/quotes", "Merchant")]
    [InlineData("/api/v1/quotes", "Courier")]
    [InlineData("/api/v1/quotes", "Admin")]
    [InlineData("/api/v1/parcels", "Merchant")]
    [InlineData("/api/v1/parcels", "Courier")]
    [InlineData("/api/v1/parcels", "Admin")]
    public async Task TrustedNonCustomerCannotElevateWithCustomerHeadersOrUnsignedToken(string route, string role)
    {
        using var factory = new QuoteApiFactory(null,
            DeliveryTestAuthentication.Principal(DeliveryTestAuthentication.NonCustomerId.ToString("D"), role));
        using var client = factory.CreateClient();
        AddSpoofedIdentity(client, DeliveryTestAuthentication.CustomerId);

        using var response = await PostRawAsync(client, route, MalformedPrivateInput, NewKey());

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Theory]
    [InlineData("/api/v1/quotes", "duplicate-subject", HttpStatusCode.Unauthorized)]
    [InlineData("/api/v1/parcels", "duplicate-subject", HttpStatusCode.Unauthorized)]
    [InlineData("/api/v1/quotes", "multiple-authenticated-identities", HttpStatusCode.Unauthorized)]
    [InlineData("/api/v1/parcels", "multiple-authenticated-identities", HttpStatusCode.Unauthorized)]
    [InlineData("/api/v1/quotes", "unauthenticated-subject", HttpStatusCode.Unauthorized)]
    [InlineData("/api/v1/parcels", "unauthenticated-subject", HttpStatusCode.Unauthorized)]
    [InlineData("/api/v1/quotes", "unauthenticated-role", HttpStatusCode.Forbidden)]
    [InlineData("/api/v1/parcels", "unauthenticated-role", HttpStatusCode.Forbidden)]
    public async Task AmbiguousOrMixedIdentityClaimsCannotAuthorizeEitherWrite(string route, string condition,
        HttpStatusCode expected)
    {
        using var factory = new QuoteApiFactory(null, MixedPrincipal(condition));
        using var client = factory.CreateClient();
        AddSpoofedIdentity(client, DeliveryTestAuthentication.CustomerId);

        using var response = await PostRawAsync(client, route, MalformedPrivateInput, NewKey());

        await AssertProblemAsync(response, expected, expected == HttpStatusCode.Forbidden ? "forbidden" : "unauthorized");
    }

    [Fact]
    public async Task TrustedCustomerOwnsTheQuoteBookingHistoryAndReplayDespiteSpoofedOwnerHeaders()
    {
        var seed = await SeedAsync();
        using var ownerFactory = new QuoteApiFactory(fixture.ConnectionString, DeliveryTestAuthentication.Customer());
        using var otherFactory = new QuoteApiFactory(fixture.ConnectionString,
            DeliveryTestAuthentication.Customer(DeliveryTestAuthentication.OtherCustomerId));
        using var owner = ownerFactory.CreateClient();
        using var other = otherFactory.CreateClient();
        AddSpoofedIdentity(owner, DeliveryTestAuthentication.OtherCustomerId);
        AddSpoofedIdentity(other, DeliveryTestAuthentication.CustomerId);
        var input = new QuoteInput(
            new("Private authentication pickup", "Fictional city", "00000", seed.Pickup.Id.ToString("D")),
            new("Private authentication delivery", "Fictional city", "00000", seed.Delivery.Id.ToString("D")),
            new(2.5m, "Small"), new("Private Fictional Recipient", "0000000000"));
        using var quoteResponse = await owner.PostAsJsonAsync("/api/v1/quotes", input);
        Assert.Equal(HttpStatusCode.OK, quoteResponse.StatusCode);
        var quote = await quoteResponse.Content.ReadFromJsonAsync<QuoteResponse>();
        Assert.NotNull(quote);
        var before = await CountsAsync();
        var key = NewKey();
        var confirmation = JsonSerializer.Serialize(new { quoteId = quote.QuoteId, acceptTotal = quote.Total });
        await using (var verification = Context())
        {
            Assert.Equal(DeliveryTestAuthentication.CustomerId,
                (await verification.Quotes.SingleAsync(item => item.Id == quote.QuoteId)).OwnerId);
        }

        using (var denied = await PostRawAsync(other, "/api/v1/parcels", confirmation, key))
        {
            await AssertProblemAsync(denied, HttpStatusCode.NotFound, "not_found");
        }
        Assert.Equal(before, await CountsAsync());
        using var created = await PostRawAsync(owner, "/api/v1/parcels", confirmation, key);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var originalBody = await created.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(originalBody);
        var parcelId = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(before.Booked(), await CountsAsync());
        Guid eventId;
        string eventPayload;
        Guid replayId;
        await using (var verification = Context())
        {
            var parcel = await verification.Parcels.SingleAsync(item => item.Id == parcelId);
            var history = await verification.BookingHistory.SingleAsync(item => item.ParcelId == parcelId);
            var record = await verification.IdempotencyRecords.SingleAsync(item => item.Key == key);
            var retainedQuote = await verification.Quotes.SingleAsync(item => item.Id == quote.QuoteId);
            var message = Assert.Single(await verification.OutboxMessages.ToArrayAsync(), item => EventParcelId(item) == parcelId);
            Assert.Equal(DeliveryTestAuthentication.CustomerId, parcel.CustomerId);
            Assert.Null(parcel.MerchantId);
            Assert.Equal(DeliveryTestAuthentication.CustomerId, history.ChangedBy);
            Assert.Equal(DeliveryTestAuthentication.CustomerId.ToString("D"), record.PrincipalId);
            Assert.Equal("CreateParcel", record.Operation);
            Assert.Equal(parcelId, record.ParcelId);
            Assert.Equal(parcelId, retainedQuote.ConsumedByParcelId);
            Assert.Null(message.PublishedAt);
            eventId = message.Id;
            eventPayload = message.Payload;
            replayId = record.Id;
        }

        using (var deniedReplay = await PostRawAsync(other, "/api/v1/parcels", confirmation, key))
        {
            await AssertProblemAsync(deniedReplay, HttpStatusCode.NotFound, "not_found");
        }
        using var replay = await PostRawAsync(owner, "/api/v1/parcels", confirmation, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync());
        Assert.Equal(created.Headers.Location, replay.Headers.Location);
        Assert.Equal(before.Booked(), await CountsAsync());
        await using (var verification = Context())
        {
            Assert.False(await verification.IdempotencyRecords.AnyAsync(item => item.Key == key
                && item.PrincipalId == DeliveryTestAuthentication.OtherCustomerId.ToString("D")));
            Assert.Equal(replayId, (await verification.IdempotencyRecords.SingleAsync(item => item.Key == key)).Id);
            var retained = await verification.OutboxMessages.SingleAsync(item => item.Id == eventId);
            Assert.Equal(eventPayload, retained.Payload);
            Assert.Null(retained.PublishedAt);
        }
    }

    private static ClaimsPrincipal MixedPrincipal(string condition)
    {
        var subject = DeliveryTestAuthentication.CustomerId.ToString("D");
        var customerRole = new Claim(ClaimTypes.Role, "Customer");
        return condition switch
        {
            "duplicate-subject" => new(new ClaimsIdentity(
                [new("sub", subject), new("sub", DeliveryTestAuthentication.OtherCustomerId.ToString("D")), customerRole],
                DeliveryTestAuthentication.SchemeName)),
            "multiple-authenticated-identities" => new(
            [
                new ClaimsIdentity([new("sub", subject), customerRole], DeliveryTestAuthentication.SchemeName),
                new ClaimsIdentity([new("sub", DeliveryTestAuthentication.OtherCustomerId.ToString("D")), customerRole],
                    DeliveryTestAuthentication.SchemeName)
            ]),
            "unauthenticated-subject" => new(
            [
                new ClaimsIdentity([customerRole], DeliveryTestAuthentication.SchemeName),
                new ClaimsIdentity([new Claim("sub", subject)])
            ]),
            "unauthenticated-role" => new(
            [
                new ClaimsIdentity([new("sub", subject), new(ClaimTypes.Role, "Merchant")], DeliveryTestAuthentication.SchemeName),
                new ClaimsIdentity([customerRole])
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(condition))
        };
    }

    private static void AddSpoofedIdentity(HttpClient client, Guid owner)
    {
        client.DefaultRequestHeaders.Add("X-User-Id", owner.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Customer-Id", owner.ToString("D"));
        client.DefaultRequestHeaders.Add("X-User-Role", "Customer");
        client.DefaultRequestHeaders.Add("X-Role", "Customer");
        var header = Base64Url(JsonSerializer.Serialize(new { alg = "none", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.Serialize(new { sub = owner, roles = new[] { "Customer" } }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", header + "." + payload + ".");
    }

    private static string Base64Url(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string route, string body, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-Correlation-ID", "authentication-boundary");
        return await client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal((int)expected, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("https://example.invalid/problems/" + code, problem.RootElement.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("title").GetString()));
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-Correlation-ID")),
            problem.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("private-authentication-input", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionStrings", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
    }

    private DeliveryDbContext Context() => new(new DbContextOptionsBuilder<DeliveryDbContext>()
        .UseNpgsql(fixture.ConnectionString).Options);

    private async Task<Seed> SeedAsync()
    {
        var pickup = new DeliveryZone(Guid.NewGuid(), "Authentication pickup " + Guid.NewGuid());
        var delivery = new DeliveryZone(Guid.NewGuid(), "Authentication delivery " + Guid.NewGuid());
        var tier = new PricingTier(Guid.NewGuid(), delivery.Id, ParcelSize.Small, "test-authentication-v1", 450m, 60m);
        await using var context = Context();
        await context.Database.MigrateAsync();
        context.Zones.AddRange(pickup, delivery);
        context.PricingTiers.Add(tier);
        await context.SaveChangesAsync();
        return new(pickup, delivery);
    }

    private async Task<Counts> CountsAsync()
    {
        await using var context = Context();
        return new(await context.Quotes.CountAsync(), await context.Parcels.CountAsync(),
            await context.PriceSnapshots.CountAsync(), await context.BookingHistory.CountAsync(),
            await context.IdempotencyRecords.CountAsync(), await context.OutboxMessages.CountAsync());
    }

    private static Guid EventParcelId(OutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.Payload);
        return payload.RootElement.GetProperty("parcelId").GetGuid();
    }

    private static string NewKey() => "authentication-" + Guid.NewGuid().ToString("N");

    private sealed record Seed(DeliveryZone Pickup, DeliveryZone Delivery);

    private sealed record Counts(int Quotes, int Parcels, int Snapshots, int History, int Keys, int Outbox)
    {
        public Counts Booked() => this with
        {
            Parcels = Parcels + 1, Snapshots = Snapshots + 1, History = History + 1, Keys = Keys + 1, Outbox = Outbox + 1
        };
    }
}
