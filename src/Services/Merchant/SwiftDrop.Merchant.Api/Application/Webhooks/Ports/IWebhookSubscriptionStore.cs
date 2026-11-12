using SwiftDrop.Merchant.Api.Domain.Webhooks;

namespace SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

public interface IWebhookSubscriptionStore
{
    Task<WebhookSubscription?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<WebhookSubscription>> ListByBusinessIdAsync(Guid businessId, CancellationToken ct);
    Task<IReadOnlyList<WebhookSubscription>> ListActiveByBusinessAndEventAsync(Guid businessId, string eventName, CancellationToken ct);
    Task AddAsync(WebhookSubscription subscription, CancellationToken ct);
    Task UpdateAsync(WebhookSubscription subscription, CancellationToken ct);
}
