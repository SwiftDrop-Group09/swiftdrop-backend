namespace SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

public interface IWebhookClock
{
    DateTimeOffset UtcNow { get; }
}
