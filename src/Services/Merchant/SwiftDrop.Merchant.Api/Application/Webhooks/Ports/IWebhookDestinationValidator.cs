namespace SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

public interface IWebhookDestinationValidator
{
    Task<bool> ValidateAsync(string endpointUrl, CancellationToken ct);
}
