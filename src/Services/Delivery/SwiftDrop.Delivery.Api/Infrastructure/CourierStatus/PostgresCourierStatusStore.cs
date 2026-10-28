using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SwiftDrop.Delivery.Api.Application.CourierStatus;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.CourierStatus;

public sealed class PostgresCourierStatusStore(Func<DeliveryDbContext> contextFactory) : ICourierStatusStore
{
    public async Task<ICourierStatusTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        try
        {
            var database = contextFactory();
            transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
            return new StatusTransaction(database, transaction);
        }
        catch (Exception exception)
        {
            if (transaction is not null) await transaction.DisposeAsync();
            if (IsStorageFailure(exception)) throw Translate(exception);
            throw;
        }
    }

    private sealed class StatusTransaction(DeliveryDbContext database, IDbContextTransaction transaction) : ICourierStatusTransaction
    {
        public Task<CourierStatusState?> LoadAuthorizedAsync(Guid courierId, Guid parcelId, CancellationToken cancellationToken) => Protect(async () =>
        {
            // Lock the parcel first, then recheck assignment in a fresh READ COMMITTED
            // statement: an EXISTS evaluated before a lock wait cannot authorize a former owner.
            var rows = await database.Parcels.FromSqlInterpolated($"""
                SELECT p.* FROM parcels p
                WHERE p.id = {parcelId} AND EXISTS (
                    SELECT 1 FROM assignments a WHERE a.parcel_id = p.id AND a.courier_id = {courierId}
                    AND (a.status = 'Active' OR (p.status = 'Failed' AND a.status = 'Failed'
                        AND NOT EXISTS (SELECT 1 FROM assignments current_assignment
                            WHERE current_assignment.parcel_id = p.id AND current_assignment.status = 'Active'))))
                FOR UPDATE OF p
                """).ToListAsync(cancellationToken);
            if (rows.SingleOrDefault() is not { } parcel) return null;
            var assignments = await database.Assignments.FromSqlInterpolated($"""
                SELECT *, xmin FROM assignments WHERE parcel_id = {parcelId} AND courier_id = {courierId}
                    AND (status = 'Active' OR (status = 'Failed' AND {parcel.Status == ParcelStatus.Failed}))
                FOR UPDATE
                """).ToListAsync(cancellationToken);
            if (assignments.SingleOrDefault() is not { } assignment) return null;
            // A Failed historical row is only for exact own-result replay or terminal rejection.
            if (assignment.Status != AssignmentStatus.Active && await database.Assignments.AsNoTracking()
                .AnyAsync(item => item.ParcelId == parcelId && item.Status == AssignmentStatus.Active, cancellationToken)) return null;
            var snapshot = await database.PriceSnapshots.AsNoTracking().SingleAsync(item => item.ParcelId == parcelId, cancellationToken);
            return new CourierStatusState(parcel, assignment, snapshot);
        });

        public Task<StatusReservation> ReserveAsync(Guid courierId, CourierStatusCommand command, DateTimeOffset now,
            CancellationToken cancellationToken) => Protect(async () =>
        {
            var candidate = new IdempotencyRecord(Guid.NewGuid(), courierId.ToString("D"), CourierStatusValidator.Operation,
                command.Key, command.Fingerprint, now);
            var inserted = await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO idempotency_keys (id, principal_id, operation, key, request_fingerprint, created_at, expires_at)
                VALUES ({candidate.Id}, {candidate.PrincipalId}, {candidate.Operation}, {candidate.Key},
                    {candidate.RequestFingerprint}, {candidate.CreatedAt}, {candidate.ExpiresAt})
                ON CONFLICT (principal_id, operation, key) DO NOTHING
                """, cancellationToken);
            var record = await database.IdempotencyRecords.SingleAsync(item => item.PrincipalId == candidate.PrincipalId
                && item.Operation == candidate.Operation && item.Key == command.Key, cancellationToken);
            return new StatusReservation(inserted == 1, record);
        });

        public async Task SaveAsync(CourierStatusState state, BookingHistory history, OutboxMessage outbox,
            IdempotencyRecord reservation, CancellationToken cancellationToken)
        {
            await Protect(async () =>
            {
                if (database.Database.CurrentTransaction != transaction || database.Entry(state.Parcel).State == EntityState.Detached
                    || database.Entry(state.Assignment).State == EntityState.Detached || database.Entry(reservation).State == EntityState.Detached
                    || history.ParcelId != state.Parcel.Id || reservation.ParcelId != state.Parcel.Id)
                    throw new InvalidOperationException("Status records must belong to the owning transaction.");
                database.BookingHistory.Add(history);
                database.OutboxMessages.Add(outbox);
                return await database.SaveChangesAsync(cancellationToken);
            });
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await Protect(async () => { await transaction.CommitAsync(cancellationToken); return true; });
        }

        public async ValueTask DisposeAsync()
        {
            try { await transaction.DisposeAsync(); }
            catch (Exception exception) when (IsStorageFailure(exception)) { throw Translate(exception); }
            finally { database.ChangeTracker.Clear(); }
        }
    }

    private static async Task<T> Protect<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception exception) when (IsStorageFailure(exception)) { throw Translate(exception); }
    }

    private static bool IsStorageFailure(Exception exception) => exception is
        NpgsqlException or DbUpdateException or TimeoutException or InvalidOperationException or ArgumentException;
    private static CourierStatusStoreException Translate(Exception exception) => new(
        DeliveryDatabaseErrors.Classify(exception) == DeliveryDatabaseFailure.ConcurrentWrite
            ? CourierStatusStorageFailure.InProgress : CourierStatusStorageFailure.Unavailable, exception);
}
