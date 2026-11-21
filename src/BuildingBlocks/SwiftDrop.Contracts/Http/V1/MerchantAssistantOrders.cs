namespace SwiftDrop.Contracts.Http.V1;

public static class MerchantOrderApi
{
    public const string OrdersPath = "/api/v1/merchant/orders";
    public const string WebhooksPath = "/api/v1/merchant/webhooks";
    public const string AssistantOrdersPath = "/internal/v1/merchant/orders/assistant-summary";
    public const string ApiKeyHeader = "X-API-Key";
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const int AssistantPageSize = 10;
}

public static class MerchantWebhookEventNames
{
    public const string Created = "delivery.created";
    public const string Assigned = "delivery.assigned";
    public const string PickedUp = "delivery.picked_up";
    public const string InTransit = "delivery.in_transit";
    public const string OutForDelivery = "delivery.out_for_delivery";
    public const string Delivered = "delivery.delivered";
    public const string Failed = "delivery.failed";
    public const string Cancelled = "delivery.cancelled";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
        new[] { Created, Assigned, PickedUp, InTransit, OutForDelivery, Delivered, Failed, Cancelled });
}

public sealed record MerchantAssistantOrderPage(int TotalCount, IReadOnlyList<MerchantAssistantOrder> Items);

public sealed record MerchantAssistantOrder(string ExternalOrderId, string State, string? TrackingNumber,
    string? ParcelStatus, string RecipientCity, decimal WeightKg, decimal? PriceLkr, DateTimeOffset CreatedAt);
