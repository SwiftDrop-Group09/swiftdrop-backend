using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

using SwiftDrop.Merchant.Api.Infrastructure.Authentication;

namespace SwiftDrop.Merchant.Tests.Merchants;

public sealed class MerchantRegistrationApiTests
{
    private static MerchantApiFactory CreateFactory(ClaimsPrincipal? principal = null) =>
        new(principal ?? MerchantApiFactory.Admin(), inMemoryDbName: Guid.NewGuid().ToString());

    // ── Authentication guard ────────────────────────────────────────────────────

    [Fact]
    public async Task UnauthenticatedRequestReturns401()
    {
        using var factory = new MerchantApiFactory(new ClaimsPrincipal(), inMemoryDbName: Guid.NewGuid().ToString());
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/register",
            new { businessName = "Test", contactEmail = "a@b.com", contactPhone = "0771234567", pickupAddress = "123 St" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertProblemAsync(response, 401, "unauthorized");
    }

    // ── Input validation ────────────────────────────────────────────────────────

    [Fact]
    public async Task MissingBusinessNameReturns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/register",
            new { businessName = "", contactEmail = "a@b.com", contactPhone = "0771234567", pickupAddress = "123 St" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, 400, "validation");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("businessName", body);
    }

    [Fact]
    public async Task MissingContactEmailReturns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/register",
            new { businessName = "ACME Ltd", contactEmail = (string?)null, contactPhone = "0771234567", pickupAddress = "123 St" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, 400, "validation");
    }

    [Fact]
    public async Task MissingPickupAddressReturns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/register",
            new { businessName = "ACME Ltd", contactEmail = "acme@example.com", contactPhone = "0771234567", pickupAddress = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, 400, "validation");
    }

    [Fact]
    public async Task InvalidJsonBodyReturns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var content = new StringContent("{invalid json}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/v1/merchants/register", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, 400, "validation");
    }

    [Fact]
    public async Task NonJsonContentTypeReturns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var content = new StringContent("businessName=Test", Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync("/api/v1/merchants/register", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Successful registration ─────────────────────────────────────────────────

    [Fact]
    public async Task ValidRegistrationReturns201WithBusinessResponse()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "reg-test-001");

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/register",
            new { businessName = "ACME Logistics", contactEmail = "info@acme.com", contactPhone = "0771234567", pickupAddress = "123 Main St, Colombo" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("ACME Logistics", root.GetProperty("businessName").GetString());
        Assert.Equal("info@acme.com", root.GetProperty("contactEmail").GetString());
        Assert.Equal("Pending", root.GetProperty("status").GetString());
        Assert.NotEqual(Guid.Empty, root.GetProperty("id").GetGuid());

        // KeyHash and Salt must NOT appear in response
        Assert.False(root.TryGetProperty("keyHash", out _));
        Assert.False(root.TryGetProperty("salt", out _));
    }

    // ── Duplicate registration guard ────────────────────────────────────────────

    [Fact]
    public async Task SecondRegistrationForSameUserReturns409()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/merchants/register",
            new { businessName = "First Business", contactEmail = "first@example.com", contactPhone = "0771111111", pickupAddress = "1 Street" });

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/register",
            new { businessName = "Second Business", contactEmail = "second@example.com", contactPhone = "0772222222", pickupAddress = "2 Street" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemAsync(response, 409, "duplicate_business");
    }

    // ── Correlation ID ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CorrelationIdIsEchoed()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "test-correlation-abc123");

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/register",
            new { businessName = "B", contactEmail = (string?)null, contactPhone = "", pickupAddress = "" });

        Assert.Equal("test-correlation-abc123", Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
    }

    // ── WwwAuthenticate header on 401 ───────────────────────────────────────────

    [Fact]
    public async Task UnauthenticatedResponseIncludesBearerWwwAuthenticateHeader()
    {
        using var factory = new MerchantApiFactory(new ClaimsPrincipal(), inMemoryDbName: Guid.NewGuid().ToString(), replaceAuthentication: false);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchants/register",
            new { businessName = "X", contactEmail = "x@x.com", contactPhone = "0", pickupAddress = "X" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("https://example.invalid/problems/" + code, problem.RootElement.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("title").GetString()));
        // Never leak internals
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionStrings", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
    }
}
