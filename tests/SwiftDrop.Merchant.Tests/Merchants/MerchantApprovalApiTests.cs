using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Tests.Merchants;

public sealed class MerchantApprovalApiTests
{
    private static MerchantApiFactory CreateFactory(ClaimsPrincipal? principal = null, string? dbName = null) =>
        new(principal ?? MerchantApiFactory.Admin(), inMemoryDbName: dbName ?? Guid.NewGuid().ToString());

    // ── Authorization guards ────────────────────────────────────────────────────

    [Fact]
    public async Task UnauthenticatedRequestReturns401()
    {
        using var factory = new MerchantApiFactory(new ClaimsPrincipal(), inMemoryDbName: Guid.NewGuid().ToString());
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/v1/merchants/" + Guid.NewGuid() + "/status",
            new { status = "Approved", reason = (string?)null });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertProblemAsync(response, 401, "unauthorized");
    }

    [Fact]
    public async Task NonAdminMerchantCannotApproveReturns403()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/v1/merchants/" + Guid.NewGuid() + "/status",
            new { status = "Approved", reason = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertProblemAsync(response, 403, "forbidden");
    }

    // ── Not found ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminSettingStatusOnNonexistentBusinessReturns404()
    {
        using var factory = CreateFactory(MerchantApiFactory.Admin());
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/v1/merchants/" + Guid.NewGuid() + "/status",
            new { status = "Approved", reason = (string?)null });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertProblemAsync(response, 404, "business_not_found");
    }

    // ── Successful approval ─────────────────────────────────────────────────────

    [Fact]
    public async Task AdminApprovesAPendingBusinessReturns200WithStatusApproved()
    {
        using var factory = CreateFactory(MerchantApiFactory.Admin());
        var businessId = await SeedBusinessAsync(factory, BusinessStatus.Pending);
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync($"/api/v1/merchants/{businessId}/status",
            new { status = "Approved", reason = (string?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Approved", doc.RootElement.GetProperty("status").GetString());
        Assert.NotNull(doc.RootElement.GetProperty("approvedAt").GetString());
    }

    // ── Rejection ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminRejectsAPendingBusinessWithReasonReturns200()
    {
        using var factory = CreateFactory(MerchantApiFactory.Admin());
        var businessId = await SeedBusinessAsync(factory, BusinessStatus.Pending);
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync($"/api/v1/merchants/{businessId}/status",
            new { status = "Rejected", reason = "Documents missing." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Documents missing.", doc.RootElement.GetProperty("rejectionReason").GetString());
    }

    [Fact]
    public async Task AdminRejectsWithoutReasonReturns400()
    {
        using var factory = CreateFactory(MerchantApiFactory.Admin());
        var businessId = await SeedBusinessAsync(factory, BusinessStatus.Pending);
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync($"/api/v1/merchants/{businessId}/status",
            new { status = "Rejected", reason = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, 400, "validation");
    }

    // ── Suspension ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminSuspendsApprovedBusinessReturns200()
    {
        using var factory = CreateFactory(MerchantApiFactory.Admin());
        var businessId = await SeedBusinessAsync(factory, BusinessStatus.Approved);
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync($"/api/v1/merchants/{businessId}/status",
            new { status = "Suspended", reason = "Suspicious activity." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Suspended", doc.RootElement.GetProperty("status").GetString());
    }

    // ── Invalid transitions (409 Conflict) ─────────────────────────────────────

    [Fact]
    public async Task AdminRejectsAlreadyApprovedBusinessReturns409()
    {
        using var factory = CreateFactory(MerchantApiFactory.Admin());
        var businessId = await SeedBusinessAsync(factory, BusinessStatus.Approved);
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync($"/api/v1/merchants/{businessId}/status",
            new { status = "Rejected", reason = "Changed mind." });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemAsync(response, 409, "invalid_status_transition");
    }

    [Fact]
    public async Task AdminSuspendsPendingBusinessReturns409()
    {
        using var factory = CreateFactory(MerchantApiFactory.Admin());
        var businessId = await SeedBusinessAsync(factory, BusinessStatus.Pending);
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync($"/api/v1/merchants/{businessId}/status",
            new { status = "Suspended", reason = "Suspended while pending." });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemAsync(response, 409, "invalid_status_transition");
    }

    // ── List merchants ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminListsMerchantsReturns200()
    {
        using var factory = CreateFactory(MerchantApiFactory.Admin());
        await SeedBusinessAsync(factory, BusinessStatus.Pending);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/merchants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task NonAdminCannotListMerchantsReturns403()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/merchants");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertProblemAsync(response, 403, "forbidden");
    }

    // ── Response never contains secrets ────────────────────────────────────────

    [Fact]
    public async Task ApprovalResponseNeverContainsKeyHashOrSalt()
    {
        using var factory = CreateFactory(MerchantApiFactory.Admin());
        var businessId = await SeedBusinessAsync(factory, BusinessStatus.Pending);
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync($"/api/v1/merchants/{businessId}/status",
            new { status = "Approved" });

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("keyHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"salt\"", body, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedBusinessAsync(MerchantApiFactory factory, BusinessStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MerchantDbContext>();

        Guid userId = status == BusinessStatus.Approved || status == BusinessStatus.Suspended
            ? MerchantApiFactory.OtherId
            : MerchantApiFactory.AdminId;

        var business = Business.Register(userId, "Test Business", "test@biz.com", "0771234567",
            "1 Test St", DateTimeOffset.UtcNow);

        if (status == BusinessStatus.Approved)
            business.Approve(MerchantApiFactory.AdminId);
        else if (status == BusinessStatus.Rejected)
            business.Reject(MerchantApiFactory.AdminId, "Test rejection.");
        else if (status == BusinessStatus.Suspended)
        {
            business.Approve(MerchantApiFactory.AdminId);
            business.Suspend(MerchantApiFactory.AdminId, "Test suspension.");
        }

        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        return business.Id;
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
