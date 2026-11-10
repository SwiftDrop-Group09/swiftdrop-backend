using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Domain.Webhooks;

namespace SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;

public sealed class AcceptStatusChangedDispatchHandler(
    IWebhookSubscriptionStore subscriptionStore,
    IWebhookDispatchStore dispatchStore,
    IWebhookOrderResolver orderResolver,
    IWebhookClock clock)
{
    public async Task<(InternalDispatchResponse? Response, WebhookFailure? Failure)> HandleAsync(
        InternalDispatchPayload payload,
        CancellationToken ct)
    {
        if (payload.SchemaVersion != 1)
        {
            return (null, WebhookFailure.InvalidInput($"Unsupported schemaVersion '{payload.SchemaVersion}'. Only schemaVersion 1 is supported."));
        }

        if (payload.EventId == Guid.Empty)
        {
            return (null, WebhookFailure.InvalidInput("EventId is required."));
        }

        if (payload.MerchantId == Guid.Empty)
        {
            return (null, WebhookFailure.InvalidInput("MerchantId is required."));
        }

        var mappedEvent = WebhookEventMapping.TryMapStatusToEvent(payload.Status);
        if (mappedEvent is null)
        {
            return (new InternalDispatchResponse(payload.EventId, 0, 0, "ignored_unrecognized_status"), null);
        }

        var subscriptions = await subscriptionStore.ListActiveByBusinessAndEventAsync(payload.MerchantId, mappedEvent, ct);
        if (subscriptions.Count == 0)
        {
            return (new InternalDispatchResponse(payload.EventId, 0, 0, "no_subscriptions"), null);
        }

        var trackingNumber = await orderResolver.TryResolveTrackingNumberAsync(payload.ParcelId, payload.MerchantId, ct);
        var requiresResolution = trackingNumber is null;

        var frozenBytes = WebhookJson.SerializeExternalEvent(
            payload.EventId,
            mappedEvent,
            schemaVersion: 1,
            payload.OccurredAt,
            payload.ParcelId,
            payload.ParcelVersion,
            trackingNumber,
            payload.Status);

        var now = clock.UtcNow;
        var jobsToCreate = new List<WebhookDeliveryJob>();

        foreach (var subscription in subscriptions)
        {
            var existing = await dispatchStore.GetJobBySubscriptionAndEventAsync(subscription.Id, payload.EventId, replayGeneration: 0, ct);
            if (existing is not null)
            {
                continue;
            }

            var job = WebhookDeliveryJob.CreateAutomatic(
                payload.EventId,
                subscription.Id,
                frozenBytes,
                subscription.SecretVersion,
                requiresResolution,
                now);

            jobsToCreate.Add(job);
        }

        if (jobsToCreate.Count > 0)
        {
            await dispatchStore.AddJobsAsync(jobsToCreate, ct);
        }

        return (new InternalDispatchResponse(payload.EventId, subscriptions.Count, jobsToCreate.Count, "accepted"), null);
    }
}
