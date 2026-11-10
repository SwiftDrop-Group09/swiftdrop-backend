using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

namespace SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;

public sealed class ListWebhookAttemptsHandler(
    ICurrentMerchantContext merchantContext,
    IWebhookSubscriptionStore subscriptionStore,
    IWebhookDispatchStore dispatchStore)
{
    public async Task<(WebhookAttemptPageResponse? Response, WebhookFailure? Failure)> HandleAsync(
        Guid subscriptionId,
        Guid? jobId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        if (!merchantContext.IsAuthenticated)
        {
            return (null, WebhookFailure.Unauthenticated("Authentication is required."));
        }

        if (!merchantContext.IsApprovedMerchant || !merchantContext.CanManageWebhooks || !merchantContext.BusinessId.HasValue)
        {
            return (null, WebhookFailure.Forbidden("Only approved merchants may view webhook attempts."));
        }

        var businessId = merchantContext.BusinessId.Value;
        var subscription = await subscriptionStore.GetByIdAsync(subscriptionId, ct);
        if (subscription is null || subscription.BusinessId != businessId)
        {
            return (null, WebhookFailure.NotFound("Webhook subscription not found."));
        }

        var clampedPage = page < 1 ? 1 : page;
        var clampedPageSize = pageSize is < 1 or > 100 ? 20 : pageSize;
        var skip = (clampedPage - 1) * clampedPageSize;

        var totalCount = await dispatchStore.CountAttemptsBySubscriptionAsync(subscriptionId, jobId, ct);
        var attempts = await dispatchStore.ListAttemptsBySubscriptionAsync(subscriptionId, jobId, skip, clampedPageSize, ct);

        var items = attempts.Select(a => new WebhookAttemptResponse(
            a.Id,
            a.JobId,
            a.AttemptNumber,
            a.RequestedAt,
            a.ResponseStatusCode,
            a.DurationMs,
            a.NextRetryAt,
            a.FailureReason)).ToList();

        return (new WebhookAttemptPageResponse(items, totalCount, clampedPage, clampedPageSize), null);
    }
}
