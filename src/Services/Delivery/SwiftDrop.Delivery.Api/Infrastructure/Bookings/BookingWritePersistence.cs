using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.Bookings;

/// <summary>Flushes shared booking records inside the owning adapter's explicit transaction.</summary>
public static class BookingWritePersistence
{
    /// <remarks>
    /// The booking-scoped context also flushes any tracked reservation/quote changes.
    /// This helper does not authorize, reserve identities, commit, roll back or publish.
    /// The caller owns those policies and translates persistence failures.
    /// </remarks>
    public static Task SaveAsync(DeliveryDbContext database, BookingWriteSet write,
        CancellationToken cancellationToken = default)
    {
        if (database.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Booking records require an explicit owning transaction.");
        }

        database.Parcels.Add(write.Parcel);
        database.PriceSnapshots.Add(write.Snapshot);
        database.BookingHistory.Add(write.History);
        database.OutboxMessages.Add(write.Outbox);
        return database.SaveChangesAsync(cancellationToken);
    }
}
