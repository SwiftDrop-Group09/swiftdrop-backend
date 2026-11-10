namespace SwiftDrop.Merchant.Api.Domain.Webhooks;

public enum WebhookStatus
{
    Pending,
    PendingResolution,
    Processing,
    Delivered,
    Failed,
    Cancelled
}
