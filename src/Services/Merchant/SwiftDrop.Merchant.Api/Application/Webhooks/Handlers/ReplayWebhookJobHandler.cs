using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Domain.Webhooks;

namespace SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;

public sealed class ReplayWebhookJobHandler(
    ICurrentMerchantContext merchantContext,
    IWebhookSubscriptionStore subscriptionStore,
    IWebhookDispatchStore dispatchStore,
    IWebhookClock clock)
{
    public async Task<(WebhookJobResponse? Response, WebhookFailure? Failure)> HandleAsync(
        Guid subscriptionId,
        ReplayWebhookRequest request,
        CancellationToken ct)
    {
        if (!merchantContext.IsAuthenticated)
        {
            return (null, WebhookFailure.Unauthenticated("Authentication is required."));
        }

        if (!merchantContext.IsApprovedMerchant || !merchantContext.CanManageWebhooks || !merchantContext.BusinessId.HasValue)
        {
            return (null, WebhookFailure.Forbidden("Only approved merchants may replay webhooks."));
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return (null, WebhookFailure.InvalidInput("A business justification reason is required for manual replay."));
        }

        var businessId = merchantContext.BusinessId.Value;
        var subscription = await subscriptionStore.GetByIdAsync(subscriptionId, ct);
        if (subscription is null || subscription.BusinessId != businessId)
        {
            return (null, WebhookFailure.NotFound("Webhook subscription not found."));
        }

        WebhookDeliveryJob? failedJob = null;
        if (request.JobId.HasValue)
        {
            failedJob = await dispatchStore.GetJobByIdAsync(request.JobId.Value, ct);
        }
        else if (request.EventId.HasValue)
        {
            failedJob = await dispatchStore.GetLatestJobByEventAsync(subscriptionId, request.EventId.Value, ct);
        }
        else
        {
            return (null, WebhookFailure.InvalidInput("Either eventId or jobId must be specified for replay."));
        }

        if (failedJob is null || failedJob.SubscriptionId != subscriptionId)
        {
            return (null, WebhookFailure.NotFound("Webhook job not found."));
        }

        if (failedJob.Status != WebhookStatus.Failed)
        {
            return (null, WebhookFailure.Conflict($"Only failed webhook jobs can be replayed. Current status is '{failedJob.Status}'."));
        }

        var actorId = merchantContext.ActorId ?? businessId;
        var (replayJob, audit) = failedJob.CreateReplay(actorId, businessId, request.Reason, clock.UtcNow);

        await dispatchStore.AddJobsAsync([replayJob], ct);
        await dispatchStore.RecordReplayAuditAsync(audit, ct);

        var response = new WebhookJobResponse(
            replayJob.Id,
            replayJob.EventId,
            replayJob.SubscriptionId,
            replayJob.ReplayGeneration,
            replayJob.Status.ToString(),
            replayJob.AttemptCount,
            replayJob.DueAt,
            replayJob.SafeErrorCode,
            replayJob.CreatedAt);

        return (response, null);
    }
}
