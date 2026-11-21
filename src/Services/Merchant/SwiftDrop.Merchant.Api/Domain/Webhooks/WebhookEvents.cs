using SwiftDrop.Contracts.Http.V1;

namespace SwiftDrop.Merchant.Api.Domain.Webhooks;

public static class WebhookEvents
{
    public const string DeliveryCreated = MerchantWebhookEventNames.Created;
    public const string DeliveryAssigned = MerchantWebhookEventNames.Assigned;
    public const string DeliveryPickedUp = MerchantWebhookEventNames.PickedUp;
    public const string DeliveryInTransit = MerchantWebhookEventNames.InTransit;
    public const string DeliveryOutForDelivery = MerchantWebhookEventNames.OutForDelivery;
    public const string DeliveryDelivered = MerchantWebhookEventNames.Delivered;
    public const string DeliveryFailed = MerchantWebhookEventNames.Failed;
    public const string DeliveryCancelled = MerchantWebhookEventNames.Cancelled;

    public static readonly IReadOnlySet<string> All = MerchantWebhookEventNames.All.ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> ValidateAndNormalize(IEnumerable<string> requestedEvents)
    {
        ArgumentNullException.ThrowIfNull(requestedEvents);

        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ev in requestedEvents)
        {
            if (string.IsNullOrWhiteSpace(ev))
            {
                continue;
            }

            var trimmed = ev.Trim().ToLowerInvariant();
            if (!All.Contains(trimmed))
            {
                throw new ArgumentException($"Invalid webhook event '{ev}'. Supported events: {string.Join(", ", All)}", nameof(requestedEvents));
            }

            normalized.Add(trimmed);
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException("At least one valid webhook event must be selected.", nameof(requestedEvents));
        }

        return normalized;
    }
}
