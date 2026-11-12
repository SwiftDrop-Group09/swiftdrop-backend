using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

namespace SwiftDrop.Merchant.Api.Infrastructure.Webhooks;

public sealed class StubWebhookOrderResolver : IWebhookOrderResolver
{
    public Task<string?> TryResolveTrackingNumberAsync(Guid parcelId, Guid businessId, CancellationToken ct)
    {
        return Task.FromResult<string?>(null);
    }
}
