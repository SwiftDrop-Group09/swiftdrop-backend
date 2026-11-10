using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

namespace SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;

public sealed class ListWebhookSubscriptionsHandler(
    ICurrentMerchantContext merchantContext,
    IWebhookSubscriptionStore subscriptionStore)
{
    public async Task<(WebhookPageResponse? Response, WebhookFailure? Failure)> HandleAsync(
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
            return (null, WebhookFailure.Forbidden("Only approved merchants may view webhooks."));
        }

        var clampedPage = page < 1 ? 1 : page;
        var clampedPageSize = pageSize is < 1 or > 100 ? 20 : pageSize;

        var businessId = merchantContext.BusinessId.Value;
        var all = await subscriptionStore.ListByBusinessIdAsync(businessId, ct);

        var totalCount = all.Count;
        var items = all
            .OrderByDescending(s => s.CreatedAt)
            .Skip((clampedPage - 1) * clampedPageSize)
            .Take(clampedPageSize)
            .Select(s => new WebhookSubscriptionResponse(
                s.Id,
                s.EndpointUrl,
                s.SubscribedEvents.OrderBy(e => e).ToList(),
                s.IsActive,
                s.SecretVersion,
                s.CreatedAt,
                s.UpdatedAt))
            .ToList();

        return (new WebhookPageResponse(items, totalCount, clampedPage, clampedPageSize), null);
    }
}
