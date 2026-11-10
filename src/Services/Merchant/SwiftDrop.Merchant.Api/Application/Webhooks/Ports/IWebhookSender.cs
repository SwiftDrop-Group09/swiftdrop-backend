using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;

namespace SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

public interface IWebhookSender
{
    Task<WebhookSendResult> SendAsync(
        string endpointUrl,
        byte[] bodyBytes,
        byte[] rawSecret,
        string eventId,
        int secretVersion,
        CancellationToken ct);
}
