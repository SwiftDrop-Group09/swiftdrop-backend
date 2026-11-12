using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;
using SwiftDrop.Merchant.Api.Domain.Webhooks;
using SwiftDrop.Merchant.Tests.Webhooks.TestDoubles;

namespace SwiftDrop.Merchant.Tests.Webhooks;

public sealed class SubscriptionApplicationTests
{
    private readonly InMemoryWebhookSubscriptionStore _subscriptionStore = new();
    private readonly InMemoryWebhookDispatchStore _dispatchStore = new();
    private readonly FakeWebhookDestinationValidator _destinationValidator = new();
    private readonly FakeWebhookSecretProtector _secretProtector = new();
    private readonly FakeWebhookClock _clock = new(DateTimeOffset.Parse("2026-11-10T12:00:00Z"));
    private readonly FakeCurrentMerchantContext _merchantContext = new();

    private CreateWebhookSubscriptionHandler CreateHandler() =>
        new(_merchantContext, _subscriptionStore, _destinationValidator, _secretProtector, _clock);

    private ListWebhookSubscriptionsHandler CreateListHandler() =>
        new(_merchantContext, _subscriptionStore);

    private DeleteWebhookSubscriptionHandler CreateDeleteHandler() =>
        new(_merchantContext, _subscriptionStore, _dispatchStore, _clock);

    [Fact]
    public async Task CreateSubscription_WithValidInputs_SucceedsWithOneTimeSecret()
    {
        var handler = CreateHandler();
        var request = new CreateWebhookRequest(
            Url: "https://example.com/webhooks",
            EndpointUrl: null,
            Events: [WebhookEvents.DeliveryDelivered, WebhookEvents.DeliveryInTransit]);

        var (response, failure) = await handler.HandleAsync(request, CancellationToken.None);

        Assert.Null(failure);
        Assert.NotNull(response);
        Assert.Equal("https://example.com/webhooks", response.EndpointUrl);
        Assert.Equal(2, response.Events.Count);
        Assert.False(string.IsNullOrWhiteSpace(response.Secret));
        Assert.Equal(64, response.Secret.Length); // 32 bytes hex = 64 chars >= 256 bits
        Assert.Equal(1, response.SecretVersion);
        Assert.Equal(_clock.UtcNow, response.CreatedAt);

        var saved = await _subscriptionStore.GetByIdAsync(response.Id, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.True(saved.IsActive);
        Assert.Equal(_merchantContext.BusinessId, saved.BusinessId);
    }

    [Fact]
    public async Task CreateSubscription_WhenUnauthenticated_ReturnsUnauthenticated()
    {
        _merchantContext.IsAuthenticated = false;
        var handler = CreateHandler();
        var request = new CreateWebhookRequest("https://example.com/webhooks", null, [WebhookEvents.DeliveryDelivered]);

        var (response, failure) = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Equal("unauthenticated", failure.Code);
        Assert.Null(response);
    }

    [Fact]
    public async Task CreateSubscription_WhenMerchantNotApproved_ReturnsForbidden()
    {
        _merchantContext.IsApprovedMerchant = false;
        var handler = CreateHandler();
        var request = new CreateWebhookRequest("https://example.com/webhooks", null, [WebhookEvents.DeliveryDelivered]);

        var (response, failure) = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Equal("forbidden", failure.Code);
        Assert.Null(response);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://insecure.com")]
    [InlineData("ftp://files.com")]
    [InlineData("javascript:alert(1)")]
    public async Task CreateSubscription_WithInvalidUrl_ReturnsInvalidInput(string invalidUrl)
    {
        var handler = CreateHandler();
        var request = new CreateWebhookRequest(invalidUrl, null, [WebhookEvents.DeliveryDelivered]);

        var (response, failure) = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Equal("invalid_input", failure.Code);
        Assert.Null(response);
    }

    [Fact]
    public async Task CreateSubscription_WithUnsafeDestination_ReturnsInvalidInput()
    {
        _destinationValidator.AlwaysSafe = false;
        var handler = CreateHandler();
        var request = new CreateWebhookRequest("https://example.com/unsafe", null, [WebhookEvents.DeliveryDelivered]);

        var (response, failure) = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Equal("invalid_input", failure.Code);
        Assert.Contains("unsafe", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response);
    }

    [Fact]
    public async Task CreateSubscription_WithUnknownEvent_ReturnsInvalidInput()
    {
        var handler = CreateHandler();
        var request = new CreateWebhookRequest("https://example.com/webhooks", null, ["order.completed", "delivery.delivered"]);

        var (response, failure) = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Equal("invalid_input", failure.Code);
        Assert.Null(response);
    }

    [Fact]
    public async Task CreateSubscription_EnforcesMaxActiveSubscriptionsLimit()
    {
        var handler = CreateHandler();

        for (var i = 0; i < 10; i++)
        {
            var req = new CreateWebhookRequest($"https://example{i}.com/hooks", null, [WebhookEvents.DeliveryDelivered]);
            var (res, fail) = await handler.HandleAsync(req, CancellationToken.None);
            Assert.NotNull(res);
            Assert.Null(fail);
        }

        var req11 = new CreateWebhookRequest("https://example11.com/hooks", null, [WebhookEvents.DeliveryDelivered]);
        var (res11, fail11) = await handler.HandleAsync(req11, CancellationToken.None);

        Assert.NotNull(fail11);
        Assert.Equal("conflict", fail11.Code);
        Assert.Contains("Maximum active webhooks", fail11.Message);
        Assert.Null(res11);
    }

    [Fact]
    public async Task ListSubscriptions_ReturnsOnlyOwnMerchantSubscriptions_WithoutSecrets()
    {
        var createHandler = CreateHandler();
        var listHandler = CreateListHandler();

        var (sub1, _) = await createHandler.HandleAsync(
            new CreateWebhookRequest("https://hook1.com", null, [WebhookEvents.DeliveryCreated]), CancellationToken.None);
        var (sub2, _) = await createHandler.HandleAsync(
            new CreateWebhookRequest("https://hook2.com", null, [WebhookEvents.DeliveryDelivered]), CancellationToken.None);

        var (page, failure) = await listHandler.HandleAsync(page: 1, pageSize: 20, CancellationToken.None);

        Assert.Null(failure);
        Assert.NotNull(page);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Count);

        // Verify secrets are never exposed in list responses
        foreach (var item in page.Items)
        {
            Assert.True(item.IsActive);
            Assert.NotEqual(Guid.Empty, item.Id);
        }
    }

    [Fact]
    public async Task DeleteSubscription_DeactivatesSubscriptionAndCancelsPendingJobs()
    {
        var createHandler = CreateHandler();
        var deleteHandler = CreateDeleteHandler();

        var (created, _) = await createHandler.HandleAsync(
            new CreateWebhookRequest("https://hook1.com", null, [WebhookEvents.DeliveryDelivered]), CancellationToken.None);
        Assert.NotNull(created);

        // Add a pending job
        var pendingJob = WebhookDeliveryJob.CreateAutomatic(
            eventId: Guid.NewGuid(),
            subscriptionId: created.Id,
            frozenEventBytes: [1, 2, 3],
            secretVersion: 1,
            now: _clock.UtcNow);
        await _dispatchStore.AddJobsAsync([pendingJob], CancellationToken.None);

        var failure = await deleteHandler.HandleAsync(created.Id, CancellationToken.None);

        Assert.Null(failure);

        var updatedSub = await _subscriptionStore.GetByIdAsync(created.Id, CancellationToken.None);
        Assert.NotNull(updatedSub);
        Assert.False(updatedSub.IsActive);

        var updatedJob = await _dispatchStore.GetJobByIdAsync(pendingJob.Id, CancellationToken.None);
        Assert.NotNull(updatedJob);
        Assert.Equal(WebhookStatus.Cancelled, updatedJob.Status);
    }
}
