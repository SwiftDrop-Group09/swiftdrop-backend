using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Domain.Webhooks;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Tests.Webhooks;

public sealed class WebhookApiIntegrationTests
{
    private sealed class AlwaysSafeDestinationValidator : IWebhookDestinationValidator
    {
        public Task<bool> ValidateAsync(string endpointUrl, CancellationToken ct) => Task.FromResult(true);
    }

    private static MerchantApiFactory CreateFactory(
        ClaimsPrincipal? principal = null,
        string? dbName = null,
        Action<IServiceCollection>? configureServices = null)
    {
        return new MerchantApiFactory(
            principal ?? MerchantApiFactory.Merchant(),
            inMemoryDbName: dbName ?? Guid.NewGuid().ToString(),
            configureServices: services =>
            {
                services.AddSingleton<IWebhookDestinationValidator, AlwaysSafeDestinationValidator>();
                configureServices?.Invoke(services);
            });
    }

    private static async Task<Business> SeedBusinessAsync(
        MerchantApiFactory factory,
        Guid userId,
        BusinessStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MerchantDbContext>();
        var business = Business.Register(
            userId,
            "Test Merchant " + userId.ToString()[..8],
            $"merchant_{userId:N}@test.invalid",
            "+94770000000",
            "123 Sea Street, Colombo 03");

        if (status == BusinessStatus.Approved)
        {
            business.Approve(MerchantApiFactory.AdminId);
        }
        else if (status == BusinessStatus.Rejected)
        {
            business.Reject(MerchantApiFactory.AdminId, "Policy violation");
        }
        else if (status == BusinessStatus.Suspended)
        {
            business.Approve(MerchantApiFactory.AdminId);
            business.Suspend(MerchantApiFactory.AdminId, "Suspended for review");
        }

        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        return business;
    }

    [Fact]
    public async Task CreateWebhook_WhenUnauthenticated_Returns401()
    {
        using var factory = new MerchantApiFactory(new ClaimsPrincipal(), inMemoryDbName: Guid.NewGuid().ToString());
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchant/webhooks",
            new { url = "https://receiver.example.com/webhooks", events = new[] { "delivery.delivered" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateWebhook_WhenMerchantNotApproved_Returns403()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        await SeedBusinessAsync(factory, MerchantApiFactory.MerchantId, BusinessStatus.Pending);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchant/webhooks",
            new { url = "https://receiver.example.com/webhooks", events = new[] { "delivery.delivered" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateWebhook_WhenApprovedMerchant_Returns201WithSecret()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        await SeedBusinessAsync(factory, MerchantApiFactory.MerchantId, BusinessStatus.Approved);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/merchant/webhooks",
            new { url = "https://receiver.example.com/webhooks", events = new[] { "delivery.delivered", "delivery.created" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<OneTimeWebhookResponse>();
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal("https://receiver.example.com/webhooks", result.EndpointUrl);
        Assert.Equal(2, result.Events.Count);
        Assert.False(string.IsNullOrWhiteSpace(result.Secret));
        Assert.Equal(64, result.Secret.Length); // 256 bits = 64 hex chars
    }

    [Fact]
    public async Task ListWebhooks_ReturnsSubscriptionsWithoutSecret()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        await SeedBusinessAsync(factory, MerchantApiFactory.MerchantId, BusinessStatus.Approved);
        using var client = factory.CreateClient();

        // Create 2 webhooks
        await client.PostAsJsonAsync("/api/v1/merchant/webhooks",
            new { url = "https://hook1.example.com/webhooks", events = new[] { "delivery.delivered" } });
        await client.PostAsJsonAsync("/api/v1/merchant/webhooks",
            new { url = "https://hook2.example.com/webhooks", events = new[] { "delivery.created" } });

        using var response = await client.GetAsync("/api/v1/merchant/webhooks?page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<WebhookPageResponse>();
        Assert.NotNull(page);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Count);

        // Ensure secrets are NOT serialized
        var rawJson = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"secret\":", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"secretCiphertext\":", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteWebhook_DeactivatesSubscription()
    {
        using var factory = CreateFactory(MerchantApiFactory.Merchant());
        await SeedBusinessAsync(factory, MerchantApiFactory.MerchantId, BusinessStatus.Approved);
        using var client = factory.CreateClient();

        var createRes = await client.PostAsJsonAsync("/api/v1/merchant/webhooks",
            new { url = "https://hook1.example.com/webhooks", events = new[] { "delivery.delivered" } });
        var created = await createRes.Content.ReadFromJsonAsync<OneTimeWebhookResponse>();
        Assert.NotNull(created);

        using var deleteRes = await client.DeleteAsync($"/api/v1/merchant/webhooks/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);

        // Verify inactive in list
        var listRes = await client.GetAsync("/api/v1/merchant/webhooks");
        var page = await listRes.Content.ReadFromJsonAsync<WebhookPageResponse>();
        Assert.NotNull(page);
        Assert.False(page.Items.First(i => i.Id == created.Id).IsActive);
    }

    [Fact]
    public async Task CrossMerchantIsolation_MerchantACannotDeleteMerchantBWebhook()
    {
        var dbName = Guid.NewGuid().ToString();

        // Merchant B creates a webhook
        var factoryB = CreateFactory(MerchantApiFactory.Principal(MerchantApiFactory.OtherId, "Merchant"), dbName: dbName);
        await SeedBusinessAsync(factoryB, MerchantApiFactory.OtherId, BusinessStatus.Approved);
        var clientB = factoryB.CreateClient();
        var createRes = await clientB.PostAsJsonAsync("/api/v1/merchant/webhooks",
            new { url = "https://hook-b.example.com/webhooks", events = new[] { "delivery.delivered" } });
        var webhookB = await createRes.Content.ReadFromJsonAsync<OneTimeWebhookResponse>();
        Assert.NotNull(webhookB);

        // Merchant A attempts to delete Merchant B's webhook -> 404
        var factoryA = CreateFactory(MerchantApiFactory.Principal(MerchantApiFactory.MerchantId, "Merchant"), dbName: dbName);
        await SeedBusinessAsync(factoryA, MerchantApiFactory.MerchantId, BusinessStatus.Approved);
        var clientA = factoryA.CreateClient();

        var deleteRes = await clientA.DeleteAsync($"/api/v1/merchant/webhooks/{webhookB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, deleteRes.StatusCode);
    }

    [Fact]
    public async Task InternalDispatch_WhenValidServiceToken_CreatesJobAndReturns202()
    {
        var dbName = Guid.NewGuid().ToString();

        // Approved merchant registers a webhook
        var merchantFactory = CreateFactory(MerchantApiFactory.Merchant(), dbName: dbName);
        var business = await SeedBusinessAsync(merchantFactory, MerchantApiFactory.MerchantId, BusinessStatus.Approved);
        var merchantClient = merchantFactory.CreateClient();

        await merchantClient.PostAsJsonAsync("/api/v1/merchant/webhooks",
            new { url = "https://receiver.example.com/webhooks", events = new[] { "delivery.delivered" } });

        // Service token with aud=merchant-internal and scope=webhooks:dispatch
        var serviceIdentity = new ClaimsIdentity([
            new("sub", "worker-notification-service"),
            new("aud", "merchant-internal"),
            new("scope", "webhooks:dispatch")
        ], MerchantApiFactory.TestSchemeName);

        var workerFactory = CreateFactory(new ClaimsPrincipal(serviceIdentity), dbName: dbName);
        var workerClient = workerFactory.CreateClient();

        var eventId = Guid.NewGuid();
        var payload = new InternalDispatchPayload(
            EventId: eventId,
            EventType: "StatusChanged",
            SchemaVersion: 1,
            OccurredAt: DateTimeOffset.UtcNow,
            ParcelId: Guid.NewGuid(),
            ParcelVersion: 1,
            MerchantId: business.Id,
            Status: "Delivered");

        using var dispatchRes = await workerClient.PostAsJsonAsync("/internal/v1/webhook-dispatches", payload);
        Assert.Equal(HttpStatusCode.Accepted, dispatchRes.StatusCode);

        var result = await dispatchRes.Content.ReadFromJsonAsync<InternalDispatchResponse>();
        Assert.NotNull(result);
        Assert.Equal(eventId, result.EventId);
        Assert.Equal(1, result.SubscriptionsMatched);
        Assert.Equal(1, result.JobsCreated);
        Assert.Equal("accepted", result.Status);
    }

    [Fact]
    public async Task InternalDispatch_WhenMissingAudienceOrScope_Returns403()
    {
        // Public client without aud=merchant-internal or scope=webhooks:dispatch
        var publicIdentity = new ClaimsIdentity([
            new("sub", "customer-123"),
            new("roles", "Customer")
        ], MerchantApiFactory.TestSchemeName);

        var factory = CreateFactory(new ClaimsPrincipal(publicIdentity));
        var client = factory.CreateClient();

        var payload = new InternalDispatchPayload(
            EventId: Guid.NewGuid(),
            EventType: "StatusChanged",
            SchemaVersion: 1,
            OccurredAt: DateTimeOffset.UtcNow,
            ParcelId: Guid.NewGuid(),
            ParcelVersion: 1,
            MerchantId: Guid.NewGuid(),
            Status: "Delivered");

        using var dispatchRes = await client.PostAsJsonAsync("/internal/v1/webhook-dispatches", payload);
        Assert.Equal(HttpStatusCode.Forbidden, dispatchRes.StatusCode);
    }
}
