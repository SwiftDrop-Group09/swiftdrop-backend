using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

namespace SwiftDrop.Merchant.Api.Infrastructure.Webhooks;

public sealed class SystemWebhookClock : IWebhookClock
{
    public static readonly SystemWebhookClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
