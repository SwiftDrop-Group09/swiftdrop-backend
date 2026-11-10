using System.Text.Json;
using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;
using SwiftDrop.Merchant.Api.Domain.Webhooks;
using SwiftDrop.Merchant.Tests.Webhooks.TestDoubles;

namespace SwiftDrop.Merchant.Tests.Webhooks;

public sealed class DispatchAcceptanceApplicationTests
{
    private readonly InMemoryWebhookSubscriptionStore _subscriptionStore = new();
    private readonly InMemoryWebhookDispatchStore _dispatchStore = new();
    private readonly FakeWebhookOrderResolver _orderResolver = new();
    private readonly FakeWebhookClock _clock = new(DateTimeOffset.Parse("2026-11-10T12:00:00Z"));

    private AcceptStatusChangedDispatchHandler CreateHandler() =>
        new(_subscriptionStore, _dispatchStore, _orderResolver, _clock);

    [Fact]
    public async Task HandleAsync_WithActiveSubscriptionAndResolvedTracking_CreatesPendingJob()
    {
        var merchantId = Guid.NewGuid();
        var parcelId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _orderResolver.ParcelTrackingMap[parcelId] = "TRK-COL-00123";

        var subscription = WebhookSubscription.Create(
            merchantId,
            "https://merchant.example.com/webhooks",
            [WebhookEvents.DeliveryDelivered],
            secretCiphertext: [1, 2, 3],
            secretNonce: new byte[12],
            secretTag: new byte[16],
            keyVersion: 1,
            _clock.UtcNow);
        await _subscriptionStore.AddAsync(subscription, CancellationToken.None);

        var handler = CreateHandler();
        var payload = new InternalDispatchPayload(
            EventId: eventId,
            EventType: "StatusChanged",
            SchemaVersion: 1,
            OccurredAt: _clock.UtcNow,
            ParcelId: parcelId,
            ParcelVersion: 3,
            MerchantId: merchantId,
            Status: "Delivered");

        var (response, failure) = await handler.HandleAsync(payload, CancellationToken.None);

        Assert.Null(failure);
        Assert.NotNull(response);
        Assert.Equal(eventId, response.EventId);
        Assert.Equal(1, response.SubscriptionsMatched);
        Assert.Equal(1, response.JobsCreated);
        Assert.Equal("accepted", response.Status);

        Assert.Single(_dispatchStore.Jobs);
        var job = _dispatchStore.Jobs[0];
        Assert.Equal(eventId, job.EventId);
        Assert.Equal(subscription.Id, job.SubscriptionId);
        Assert.Equal(0, job.ReplayGeneration);
        Assert.Equal(WebhookStatus.Pending, job.Status);

        // Verify frozen payload contains trackingNumber
        using var doc = JsonDocument.Parse(job.FrozenEventBytes);
        var root = doc.RootElement;
        Assert.Equal(eventId.ToString(), root.GetProperty("eventId").GetString());
        Assert.Equal("delivery.delivered", root.GetProperty("eventType").GetString());
        Assert.Equal("TRK-COL-00123", root.GetProperty("trackingNumber").GetString());
        Assert.Equal("Delivered", root.GetProperty("status").GetString());
    }

    [Fact]
    public async Task HandleAsync_WhenTrackingNotYetAvailable_SetsPendingResolution()
    {
        var merchantId = Guid.NewGuid();
        var parcelId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        // No tracking number mapped in _orderResolver
        var subscription = WebhookSubscription.Create(
            merchantId,
            "https://merchant.example.com/webhooks",
            [WebhookEvents.DeliveryDelivered],
            secretCiphertext: [1, 2, 3],
            secretNonce: new byte[12],
            secretTag: new byte[16],
            keyVersion: 1,
            _clock.UtcNow);
        await _subscriptionStore.AddAsync(subscription, CancellationToken.None);

        var handler = CreateHandler();
        var payload = new InternalDispatchPayload(
            EventId: eventId,
            EventType: "StatusChanged",
            SchemaVersion: 1,
            OccurredAt: _clock.UtcNow,
            ParcelId: parcelId,
            ParcelVersion: 1,
            MerchantId: merchantId,
            Status: "Delivered");

        var (response, failure) = await handler.HandleAsync(payload, CancellationToken.None);

        Assert.Null(failure);
        Assert.NotNull(response);
        Assert.Equal(1, response.JobsCreated);

        var job = _dispatchStore.Jobs[0];
        Assert.Equal(WebhookStatus.PendingResolution, job.Status);
    }

    [Fact]
    public async Task HandleAsync_DuplicateStatusChangedDelivery_IsIdempotent()
    {
        var merchantId = Guid.NewGuid();
        var parcelId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var subscription = WebhookSubscription.Create(
            merchantId,
            "https://merchant.example.com/webhooks",
            [WebhookEvents.DeliveryDelivered],
            secretCiphertext: [1, 2, 3],
            secretNonce: new byte[12],
            secretTag: new byte[16],
            keyVersion: 1,
            _clock.UtcNow);
        await _subscriptionStore.AddAsync(subscription, CancellationToken.None);

        var handler = CreateHandler();
        var payload = new InternalDispatchPayload(
            EventId: eventId,
            EventType: "StatusChanged",
            SchemaVersion: 1,
            OccurredAt: _clock.UtcNow,
            ParcelId: parcelId,
            ParcelVersion: 2,
            MerchantId: merchantId,
            Status: "Delivered");

        // First delivery
        var (res1, _) = await handler.HandleAsync(payload, CancellationToken.None);
        Assert.Equal(1, res1!.JobsCreated);

        // Duplicate delivery with same eventId
        var (res2, _) = await handler.HandleAsync(payload, CancellationToken.None);
        Assert.Equal(0, res2!.JobsCreated);
        Assert.Equal(1, res2.SubscriptionsMatched);

        Assert.Single(_dispatchStore.Jobs);
    }

    [Fact]
    public async Task HandleAsync_WhenSchemaVersionNot1_ReturnsInvalidInput()
    {
        var handler = CreateHandler();
        var payload = new InternalDispatchPayload(
            EventId: Guid.NewGuid(),
            EventType: "StatusChanged",
            SchemaVersion: 2, // invalid
            OccurredAt: _clock.UtcNow,
            ParcelId: Guid.NewGuid(),
            ParcelVersion: 1,
            MerchantId: Guid.NewGuid(),
            Status: "Delivered");

        var (response, failure) = await handler.HandleAsync(payload, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Equal("invalid_input", failure.Code);
        Assert.Null(response);
    }

    [Fact]
    public async Task HandleAsync_WhenStatusUnrecognized_ReturnsIgnored()
    {
        var handler = CreateHandler();
        var payload = new InternalDispatchPayload(
            EventId: Guid.NewGuid(),
            EventType: "StatusChanged",
            SchemaVersion: 1,
            OccurredAt: _clock.UtcNow,
            ParcelId: Guid.NewGuid(),
            ParcelVersion: 1,
            MerchantId: Guid.NewGuid(),
            Status: "UnknownCustomStatus");

        var (response, failure) = await handler.HandleAsync(payload, CancellationToken.None);

        Assert.Null(failure);
        Assert.NotNull(response);
        Assert.Equal(0, response.JobsCreated);
        Assert.Equal("ignored_unrecognized_status", response.Status);
    }
}
