using Microsoft.EntityFrameworkCore;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Api.Infrastructure.Orders;

/// <summary>
/// Gives outgoing webhooks the parcel's tracking number from the merchant's own
/// order record. Matches on both parcel and business, so a webhook can never
/// carry another business's tracking number. Returns null while the order has no
/// parcel yet (the webhook then waits in PendingResolution).
/// </summary>
public sealed class InboxJobWebhookOrderResolver(MerchantDbContext database) : IWebhookOrderResolver
{
    public Task<string?> TryResolveTrackingNumberAsync(Guid parcelId, Guid businessId, CancellationToken ct) =>
        database.InboxJobs.AsNoTracking()
            .Where(job => job.ParcelId == parcelId && job.BusinessId == businessId && job.State == InboxJobState.Created)
            .Select(job => job.TrackingNumber)
            .FirstOrDefaultAsync(ct);
}
