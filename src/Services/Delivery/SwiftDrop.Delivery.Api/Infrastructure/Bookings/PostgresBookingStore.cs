using System.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.Bookings;

public sealed class PostgresBookingStore(Func<DeliveryDbContext> contextFactory) : IBookingStore
{
    public async Task<IBookingTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        try
        {
            var database = contextFactory();
            transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            // Bound waits on a concurrent reservation or quote, without changing
            // connection/session defaults for other requests or pooled connections.
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
            return new BookingTransaction(database, transaction);
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            if (IsStorageFailure(exception))
            {
                throw Translate(exception);
            }

            throw;
        }
    }

    private sealed class BookingTransaction(DeliveryDbContext database, IDbContextTransaction transaction)
        : IBookingTransaction
    {
        public async Task<BookingReservation> ReserveAsync(Guid ownerId, string key, string fingerprint,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            try
            {
                var candidate = new IdempotencyRecord(Guid.NewGuid(), ownerId.ToString("D"),
                    "CreateParcel", key, fingerprint, now);
                // ON CONFLICT waits for the other transaction to finish. At READ
                // COMMITTED the following query sees its completed response. A new
                // reservation stays uncommitted until every booking write commits.
                var inserted = await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO idempotency_keys
                        (id, principal_id, operation, key, request_fingerprint, created_at, expires_at)
                    VALUES ({candidate.Id}, {candidate.PrincipalId}, {candidate.Operation}, {candidate.Key},
                        {candidate.RequestFingerprint}, {candidate.CreatedAt}, {candidate.ExpiresAt})
                    ON CONFLICT (principal_id, operation, key) DO NOTHING
                    """, cancellationToken);
                var record = await database.IdempotencyRecords.SingleAsync(item =>
                    item.PrincipalId == candidate.PrincipalId && item.Operation == "CreateParcel"
                    && item.Key == key, cancellationToken);
                return new BookingReservation(inserted == 1, record);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                throw Translate(exception);
            }
        }

        public async Task<BookingQuote?> LoadOwnedQuoteForUpdateAsync(Guid ownerId, Guid quoteId,
            CancellationToken cancellationToken)
        {
            try
            {
                // Owner filtering precedes the lock and any private projection.
                // The lock serializes different keys attempting the same quote;
                // xmin remains an additional EF concurrency guard.
                var quotes = await database.Quotes.FromSqlInterpolated($"""
                    SELECT *, xmin FROM quotes
                    WHERE id = {quoteId} AND owner_id = {ownerId}
                    FOR UPDATE
                    """).ToListAsync(cancellationToken);
                if (quotes.SingleOrDefault() is not { } quote)
                {
                    return null;
                }

                // Activation changes do not change a still-valid retained price.
                var tier = await database.PricingTiers.AsNoTracking()
                    .SingleAsync(item => item.Id == quote.PricingTierId, cancellationToken);
                return new BookingQuote(quote, tier);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                throw Translate(exception);
            }
        }

        public async Task SaveAsync(Quote quote, BookingWriteSet write, IdempotencyRecord reservation,
            CancellationToken cancellationToken)
        {
            try
            {
                if (database.Entry(quote).State == EntityState.Detached
                    || database.Entry(reservation).State == EntityState.Detached)
                {
                    throw new InvalidOperationException("Booking state must belong to this transaction.");
                }

                database.Parcels.Add(write.Parcel);
                database.PriceSnapshots.Add(write.Snapshot);
                database.BookingHistory.Add(write.History);
                database.OutboxMessages.Add(write.Outbox);
                // Quote and reservation were loaded/tracked inside this same
                // transaction. Save their consumption/completion with the rows.
                await database.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                throw Translate(exception);
            }
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                throw Translate(exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Disposing an uncommitted transaction rolls back the reservation
                // and all writes; successful replay requires no database mutation.
                await transaction.DisposeAsync();
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                throw Translate(exception);
            }
            finally
            {
                database.ChangeTracker.Clear();
            }
        }
    }

    private static bool IsStorageFailure(Exception exception) => exception is
        NpgsqlException or DbUpdateException or TimeoutException or InvalidOperationException or ArgumentException;

    private static BookingStoreException Translate(Exception exception)
    {
        var failure = DeliveryDatabaseErrors.Classify(exception) switch
        {
            DeliveryDatabaseFailure.DuplicateTrackingNumber => BookingStorageFailure.TrackingCollision,
            DeliveryDatabaseFailure.ConcurrentWrite => BookingStorageFailure.InProgress,
            _ => BookingStorageFailure.Unavailable
        };
        return new BookingStoreException(failure, exception);
    }
}
