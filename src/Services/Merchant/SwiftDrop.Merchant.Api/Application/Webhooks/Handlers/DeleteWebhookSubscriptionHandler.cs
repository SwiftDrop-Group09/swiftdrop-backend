using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

namespace SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;

public sealed class DeleteWebhookSubscriptionHandler(
    ICurrentMerchantContext merchantContext,
    IWebhookSubscriptionStore subscriptionStore,
    IWebhookDispatchStore dispatchStore,
    IWebhookClock clock)
{
    public async Task<WebhookFailure?> HandleAsync(Guid subscriptionId, CancellationToken ct)
    {
        if (!merchantContext.IsAuthenticated)
        {
            return WebhookFailure.Unauthenticated("Authentication is required.");
        }

        if (!merchantContext.IsApprovedMerchant || !merchantContext.CanManageWebhooks || !merchantContext.BusinessId.HasValue)
        {
            return WebhookFailure.Forbidden("Only approved merchants may delete webhooks.");
        }

        var businessId = merchantContext.BusinessId.Value;
        var subscription = await subscriptionStore.GetByIdAsync(subscriptionId, ct);
        if (subscription is null || subscription.BusinessId != businessId)
        {
            return WebhookFailure.NotFound("Webhook subscription not found.");
        }

        var now = clock.UtcNow;
        subscription.Deactivate(now);
        await subscriptionStore.UpdateAsync(subscription, ct);
        await dispatchStore.CancelUnsentJobsBySubscriptionAsync(subscription.Id, now, ct);

        return null;
    }
}
