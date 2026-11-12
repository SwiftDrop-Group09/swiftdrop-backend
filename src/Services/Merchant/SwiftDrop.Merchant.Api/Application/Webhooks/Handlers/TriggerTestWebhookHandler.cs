using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Domain.Webhooks;

namespace SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;

public sealed class TriggerTestWebhookHandler(
    ICurrentMerchantContext merchantContext,
    IWebhookSubscriptionStore subscriptionStore,
    IWebhookDispatchStore dispatchStore,
    IWebhookClock clock)
{
    public async Task<(WebhookJobResponse? Response, WebhookFailure? Failure)> HandleAsync(
        Guid subscriptionId,
        CancellationToken ct)
    {
        if (!merchantContext.IsAuthenticated)
        {
            return (null, WebhookFailure.Unauthenticated("Authentication is required."));
        }

        if (!merchantContext.IsApprovedMerchant || !merchantContext.CanManageWebhooks || !merchantContext.BusinessId.HasValue)
        {
            return (null, WebhookFailure.Forbidden("Only approved merchants may trigger test webhooks."));
        }

        var businessId = merchantContext.BusinessId.Value;
        var subscription = await subscriptionStore.GetByIdAsync(subscriptionId, ct);
        if (subscription is null || subscription.BusinessId != businessId)
        {
            return (null, WebhookFailure.NotFound("Webhook subscription not found."));
        }

        if (!subscription.IsActive)
        {
            return (null, WebhookFailure.Conflict("Cannot send a test event to an inactive webhook subscription."));
        }

        var now = clock.UtcNow;
        var eventId = Guid.NewGuid();
        var sampleParcelId = Guid.NewGuid();
        var sampleTrackingNumber = $"SWD-TEST-{Random.Shared.Next(100000, 999999)}";
        var eventType = subscription.SubscribedEvents.FirstOrDefault() ?? WebhookEvents.DeliveryDelivered;

        var frozenBytes = WebhookJson.SerializeExternalEvent(
            eventId: eventId,
            eventType: eventType,
            schemaVersion: 1,
            occurredAt: now,
            parcelId: sampleParcelId,
            parcelVersion: 1,
            trackingNumber: sampleTrackingNumber,
            status: "Delivered");

        var testJob = WebhookDeliveryJob.CreateAutomatic(
            eventId: eventId,
            subscriptionId: subscription.Id,
            frozenEventBytes: frozenBytes,
            secretVersion: subscription.SecretVersion,
            requiresResolution: false,
            now: now);

        await dispatchStore.AddJobsAsync([testJob], ct);

        var response = new WebhookJobResponse(
            testJob.Id,
            testJob.EventId,
            testJob.SubscriptionId,
            testJob.ReplayGeneration,
            testJob.Status.ToString(),
            testJob.AttemptCount,
            testJob.DueAt,
            testJob.SafeErrorCode,
            testJob.CreatedAt);

        return (response, null);
    }
}
