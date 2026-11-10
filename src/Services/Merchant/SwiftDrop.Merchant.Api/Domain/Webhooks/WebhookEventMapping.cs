namespace SwiftDrop.Merchant.Api.Domain.Webhooks;

public static class WebhookEventMapping
{
    private static readonly Dictionary<string, string> StatusToEventMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Created"] = WebhookEvents.DeliveryCreated,
        ["Assigned"] = WebhookEvents.DeliveryAssigned,
        ["PickedUp"] = WebhookEvents.DeliveryPickedUp,
        ["InTransit"] = WebhookEvents.DeliveryInTransit,
        ["OutForDelivery"] = WebhookEvents.DeliveryOutForDelivery,
        ["Delivered"] = WebhookEvents.DeliveryDelivered,
        ["Failed"] = WebhookEvents.DeliveryFailed,
        ["Cancelled"] = WebhookEvents.DeliveryCancelled,
    };

    public static string? TryMapStatusToEvent(string deliveryStatus)
    {
        if (string.IsNullOrWhiteSpace(deliveryStatus))
        {
            return null;
        }

        return StatusToEventMap.TryGetValue(deliveryStatus.Trim(), out var mappedEvent)
            ? mappedEvent
            : null;
    }
}
