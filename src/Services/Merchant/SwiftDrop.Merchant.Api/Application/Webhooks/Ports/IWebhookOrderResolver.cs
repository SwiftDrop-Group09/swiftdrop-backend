namespace SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

public interface IWebhookOrderResolver
{
    Task<string?> TryResolveTrackingNumberAsync(Guid parcelId, Guid businessId, CancellationToken ct);
}
