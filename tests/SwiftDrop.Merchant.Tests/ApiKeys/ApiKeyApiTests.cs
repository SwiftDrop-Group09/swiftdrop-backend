using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Tests.ApiKeys;

public sealed class ApiKeyApiTests
{
    private static MerchantApiFactory CreateFactory(ClaimsPrincipal? principal = null, string? dbName = null) =>
        new(principal ?? MerchantApiFactory.Merchant(), inMemoryDbName: dbName ?? Guid.NewGuid().ToString());

    // ── Authorization guards ────────────────────────────────────────────────────

    [Fact]
    public async Task UnauthenticatedRequestCannotCreateApiKeyReturns401()
    {
        using var factory = new MerchantApiFactory(new ClaimsPrincipal(), inMemoryDbName: Guid.NewGuid().ToString());
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/api-keys",
            new { scopes = new[] { "orders:read" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertProblemAsync(response, 401, "unauthorized");
    }

    [Fact]
    public async Task AuthenticatedUserWithoutBusinessCannotCreateApiKeyReturns404()
    {
        using var factory = CreateFactory(MerchantApiFactory.Admin());
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/api-keys",
            new { scopes = new[] { "orders:read" } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertProblemAsync(response, 404, "business_not_found");
    }

    // ── Pending merchant cannot create API key ──────────────────────────────────

    [Fact]
    public async Task PendingMerchantCannotCreateApiKeyReturns403()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        await SeedBusinessAsync(factory, MerchantApiFactory.MerchantId, BusinessStatus.Pending);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/api-keys",
            new { scopes = new[] { "orders:read" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertProblemAsync(response, 403, "business_not_approved");
    }

    // ── Approved merchant creates API key ───────────────────────────────────────

    [Fact]
    public async Task ApprovedMerchantCreatesApiKeyReturns201WithPlaintextKey()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        await SeedBusinessAsync(factory, MerchantApiFactory.MerchantId, BusinessStatus.Approved);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/api-keys",
            new { scopes = new[] { "orders:read", "orders:write" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Response must have the one-time plaintext key
        var keyId = root.GetProperty("keyId").GetGuid();
        var apiKey = root.GetProperty("apiKey").GetString();
        Assert.NotEqual(Guid.Empty, keyId);
        Assert.False(string.IsNullOrWhiteSpace(apiKey));
        Assert.StartsWith("swd_test_", apiKey);

        // Scopes must be returned
        var scopes = root.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains("orders:read", scopes);
        Assert.Contains("orders:write", scopes);

        // KeyHash and Salt must NEVER appear in response
        Assert.DoesNotContain("keyHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"salt\"", body, StringComparison.OrdinalIgnoreCase);
    }

    // ── Invalid scopes ──────────────────────────────────────────────────────────

    [Fact]
    public async Task InvalidScopeReturns400()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        await SeedBusinessAsync(factory, MerchantApiFactory.MerchantId, BusinessStatus.Approved);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/api-keys",
            new { scopes = new[] { "not:a:real:scope" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, 400, "validation");
    }

    [Fact]
    public async Task EmptyScopeListReturns400()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        await SeedBusinessAsync(factory, MerchantApiFactory.MerchantId, BusinessStatus.Approved);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/api-keys",
            new { scopes = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, 400, "validation");
    }

    // ── List API keys ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ApprovedMerchantListsApiKeysWithoutHashOrSalt()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        await SeedBusinessAsync(factory, MerchantApiFactory.MerchantId, BusinessStatus.Approved);
        using var client = factory.CreateClient();

        // Create a key first
        await client.PostAsJsonAsync("/api/v1/merchants/api-keys",
            new { scopes = new[] { "orders:read" } });

        using var response = await client.GetAsync("/api/v1/merchants/api-keys");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetArrayLength() >= 1);

        // KeyHash and Salt must NEVER appear in list response
        Assert.DoesNotContain("keyHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"salt\"", body, StringComparison.OrdinalIgnoreCase);
        // Plaintext key must NOT appear in list response
        Assert.DoesNotContain("\"apiKey\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PendingMerchantCannotListApiKeysReturns403()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        await SeedBusinessAsync(factory, MerchantApiFactory.MerchantId, BusinessStatus.Pending);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/merchants/api-keys");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertProblemAsync(response, 403, "business_not_approved");
    }

    // ── Revoke API key ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ApprovedMerchantRevokesOwnKeyReturns204()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        await SeedBusinessAsync(factory, MerchantApiFactory.MerchantId, BusinessStatus.Approved);
        using var client = factory.CreateClient();

        // Create key
        using var createResponse = await client.PostAsJsonAsync("/api/v1/merchants/api-keys",
            new { scopes = new[] { "webhooks:manage" } });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var createBody = await createResponse.Content.ReadAsStringAsync();
        using var createDoc = JsonDocument.Parse(createBody);
        var keyId = createDoc.RootElement.GetProperty("keyId").GetGuid();

        // Revoke key
        using var revokeResponse = await client.DeleteAsync($"/api/v1/merchants/api-keys/{keyId}");
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
    }

    [Fact]
    public async Task RevokingNonexistentKeyReturns404()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        await SeedBusinessAsync(factory, MerchantApiFactory.MerchantId, BusinessStatus.Approved);
        using var client = factory.CreateClient();

        using var response = await client.DeleteAsync($"/api/v1/merchants/api-keys/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertProblemAsync(response, 404, "api_key_not_found");
    }

    [Fact]
    public async Task MerchantCannotRevokeAnotherMerchantsKeyReturns404()
    {
        var sharedDbName = Guid.NewGuid().ToString();

        // Seed a key owned by OtherId merchant in shared DB
        using var otherFactory = CreateFactory(
            MerchantApiFactory.Principal(MerchantApiFactory.OtherId, "Merchant"), sharedDbName);
        await SeedBusinessAsync(otherFactory, MerchantApiFactory.OtherId, BusinessStatus.Approved);
        using var otherClient = otherFactory.CreateClient();
        using var createResponse = await otherClient.PostAsJsonAsync("/api/v1/merchants/api-keys",
            new { scopes = new[] { "orders:read" } });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        using var createDoc = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var keyId = createDoc.RootElement.GetProperty("keyId").GetGuid();

        // Now, MerchantId connects to same shared DB and tries to revoke that key
        using var attackerFactory = CreateFactory(MerchantApiFactory.Merchant(), sharedDbName);
        await SeedBusinessAsync(attackerFactory, MerchantApiFactory.MerchantId, BusinessStatus.Approved);
        using var attackerClient = attackerFactory.CreateClient();

        using var revokeResponse = await attackerClient.DeleteAsync($"/api/v1/merchants/api-keys/{keyId}");
        Assert.Equal(HttpStatusCode.NotFound, revokeResponse.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task SeedBusinessAsync(MerchantApiFactory factory, Guid userId, BusinessStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MerchantDbContext>();

        var business = Business.Register(userId, "Test Business", "test@biz.com",
            "0771234567", "1 Test Street", DateTimeOffset.UtcNow);

        if (status == BusinessStatus.Approved)
            business.Approve(MerchantApiFactory.AdminId);
        else if (status == BusinessStatus.Rejected)
            business.Reject(MerchantApiFactory.AdminId, "Rejected for testing.");

        db.Businesses.Add(business);
        await db.SaveChangesAsync();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("https://example.invalid/problems/" + code, problem.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
    }
}
