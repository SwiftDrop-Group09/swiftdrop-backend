using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Contracts.Events.V1;
using SwiftDrop.Contracts.Http.V1;
using SwiftDrop.Delivery.Api.Application.Notifications;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.Notifications;

public sealed class PostgresNotificationContextStore(DeliveryDbContext db) : INotificationContextStore
{
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    public async Task<NotificationContextResolution> ResolveAsync(
        NotificationContextRequest reference,
        CancellationToken cancellationToken)
    {
        var outbox = await db.OutboxMessages.AsNoTracking()
            .SingleOrDefaultAsync(message => message.Id == reference.EventId, cancellationToken);
        if (outbox is null || outbox.EventType != StatusChangedV1.CanonicalEventType)
        {
            return new(false, null);
        }

        StatusChangedV1? statusEvent;
        try
        {
            statusEvent = JsonSerializer.Deserialize<StatusChangedV1>(outbox.Payload, EventJson);
        }
        catch (JsonException)
        {
            return new(false, null);
        }

        if (statusEvent is null || !statusEvent.IsValid(out _) ||
            statusEvent.EventId != reference.EventId || statusEvent.ParcelVersion != reference.ParcelVersion ||
            !Guid.TryParse(statusEvent.ParcelId, out var eventParcelId) || eventParcelId != reference.ParcelId)
        {
            return new(false, null);
        }

        var parcel = await db.Parcels.AsNoTracking()
            .Where(item => item.Id == reference.ParcelId)
            .Select(item => new { item.CustomerId, item.MerchantId })
            .SingleOrDefaultAsync(cancellationToken);
        if (parcel is null || !MerchantMatches(statusEvent.MerchantId, parcel.MerchantId))
        {
            return new(false, null);
        }

        var customerUserId = parcel.MerchantId is null ? parcel.CustomerId : null;
        return new(true, customerUserId);
    }

    private static bool MerchantMatches(string? eventMerchantId, Guid? parcelMerchantId)
    {
        if (eventMerchantId is null || parcelMerchantId is null)
        {
            return eventMerchantId is null && parcelMerchantId is null;
        }

        return Guid.TryParse(eventMerchantId, out var parsedMerchantId) && parsedMerchantId == parcelMerchantId;
    }
}
