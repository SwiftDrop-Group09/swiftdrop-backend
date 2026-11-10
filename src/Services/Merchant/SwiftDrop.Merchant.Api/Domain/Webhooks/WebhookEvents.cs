namespace SwiftDrop.Merchant.Api.Domain.Webhooks;

public static class WebhookEvents
{
    public const string DeliveryCreated = "delivery.created";
    public const string DeliveryAssigned = "delivery.assigned";
    public const string DeliveryPickedUp = "delivery.picked_up";
    public const string DeliveryInTransit = "delivery.in_transit";
    public const string DeliveryOutForDelivery = "delivery.out_for_delivery";
    public const string DeliveryDelivered = "delivery.delivered";
    public const string DeliveryFailed = "delivery.failed";
    public const string DeliveryCancelled = "delivery.cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        DeliveryCreated,
        DeliveryAssigned,
        DeliveryPickedUp,
        DeliveryInTransit,
        DeliveryOutForDelivery,
        DeliveryDelivered,
        DeliveryFailed,
        DeliveryCancelled
    };

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
