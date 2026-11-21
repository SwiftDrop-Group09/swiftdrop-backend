using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SwiftDrop.Delivery.Api.Application.Dispatch;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.Dispatch;

public sealed class PostgresAssignmentStore(Func<DeliveryDbContext> contextFactory) : IAssignmentStore
{
    public Task<AssignmentOptions?> GetOptionsAsync(Guid parcelId, DateOnly today, string timeZone, DateOnly? requestedDate,
        int page, int pageSize, bool reassign, CancellationToken cancellationToken) => Protect<AssignmentOptions?>(async () =>
    {
        var db = contextFactory();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var parcel = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == parcelId, cancellationToken);
        if (parcel is null) return null;
        var current = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(x => x.ParcelId == parcelId
            && x.Status == AssignmentStatus.Active, cancellationToken);
        if (reassign ? parcel.Status != ParcelStatus.Assigned || current is null || current.PickedUpAt is not null
            : parcel.Status != ParcelStatus.Created || current is not null)
            throw new AssignmentException(AssignmentFailure.InvalidTransition);
        var serviceDate = requestedDate ?? (reassign && current!.ServiceDate > today ? current.ServiceDate : today);
        var excludedCourier = reassign ? current!.CourierId : Guid.Empty;
        var parcelWeight = parcel.WeightKg;
        var candidatesQuery = db.Couriers.AsNoTracking().Where(c => (c.Id != excludedCourier) && c.Active && (serviceDate > today || c.OnDuty)
            && db.CourierZones.Any(z => z.CourierId == c.Id && z.ZoneId == parcel.ZoneId)
            && c.MaxWeightKg >= parcelWeight)
            .Select(c => new {
                CourierId = c.Id,
                c.BusinessNumber,
                c.OnDuty,
                c.Capacity,
                c.VehicleType,
                c.VehicleNumber,
                c.MaxWeightKg,
                ActiveCountForServiceDate = db.Assignments.Count(a => a.CourierId == c.Id && a.Status == AssignmentStatus.Active && a.ServiceDate == serviceDate)
            })
            .Where(c => c.ActiveCountForServiceDate < c.Capacity);

        var rawCandidates = await candidatesQuery.ToListAsync(cancellationToken);
        var total = rawCandidates.Count;

        var scored = rawCandidates.Select(c =>
        {
            var workloadRatio = (double)c.ActiveCountForServiceDate / Math.Max(1, c.Capacity);
            var workloadScore = (int)((1.0 - workloadRatio) * 50);

            var vehicleScore = c.VehicleType switch
            {
                VehicleType.Bicycle when parcelWeight <= 5.0m => 30,
                VehicleType.Bicycle => 10,
                VehicleType.Motorcycle when parcelWeight <= 15.0m => 30,
                VehicleType.Motorcycle => 25,
                VehicleType.Car when parcelWeight is > 10.0m and <= 50.0m => 30,
                VehicleType.Car => 20,
                VehicleType.Van when parcelWeight > 30.0m => 30,
                VehicleType.Van => 20,
                VehicleType.Truck when parcelWeight > 100.0m => 30,
                _ => 15
            };

            var headroom = Math.Clamp(c.Capacity - c.ActiveCountForServiceDate, 0, 20);
            var totalScore = Math.Clamp(workloadScore + vehicleScore + headroom, 1, 100);

            return new AssignmentCandidate(
                c.CourierId,
                c.OnDuty,
                c.ActiveCountForServiceDate,
                c.Capacity,
                "COU-" + c.BusinessNumber.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                c.VehicleType,
                c.VehicleNumber,
                c.MaxWeightKg,
                totalScore,
                false);
        }).ToList();
        var bestCourier = scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.ActiveCountForServiceDate)
            .FirstOrDefault();
        var bestCourierId = bestCourier?.CourierId;

        var items = scored
            .Select(c => c.CourierId == bestCourierId ? c with { IsRecommended = true } : c)
            .OrderBy(c => c.CourierId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

        ReassignmentContext? currentAssignment = null;
        if (current is not null)
        {
            var number = await db.Couriers.AsNoTracking().Where(c => c.Id == current.CourierId)
                .Select(c => c.BusinessNumber).SingleAsync(cancellationToken);
            currentAssignment = new(current.CourierId, current.ServiceDate,
                "COU-" + number.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
        }
        await transaction.CommitAsync(cancellationToken);
        return new(parcel.Id, parcel.Version, today, timeZone, serviceDate, items, total, page, pageSize, reassign,
            currentAssignment);
    });

    public Task<IAssignmentTransaction> BeginAsync(CancellationToken cancellationToken) => Protect<IAssignmentTransaction>(async () =>
    {
        var db = contextFactory();
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
            return new Transaction(db, transaction);
        }
        catch { await transaction.DisposeAsync(); throw; }
    });

    private sealed class Transaction(DeliveryDbContext db, IDbContextTransaction transaction) : IAssignmentTransaction
    {
        public Task<AssignmentState?> LockParcelAsync(Guid parcelId, CancellationToken cancellationToken) => Protect<AssignmentState?>(async () =>
        {
            var rows = await db.Parcels.FromSqlInterpolated($"SELECT * FROM parcels WHERE id = {parcelId} FOR UPDATE").ToListAsync(cancellationToken);
            if (rows.SingleOrDefault() is not { } parcel) return null;
            var snapshot = await db.PriceSnapshots.AsNoTracking().SingleAsync(x => x.ParcelId == parcelId, cancellationToken);
            return new(parcel, snapshot);
        });

        public Task<AssignmentReservation> ReserveAsync(Guid actor, AssignCourierCommand command, DateTimeOffset now,
            CancellationToken cancellationToken) => Protect(async () =>
        {
            var candidate = new IdempotencyRecord(Guid.NewGuid(), actor.ToString("D"), AssignmentValidator.Operation,
                command.Key, command.Fingerprint, now);
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO idempotency_keys (id, principal_id, operation, key, request_fingerprint, created_at, expires_at)
                VALUES ({candidate.Id}, {candidate.PrincipalId}, {candidate.Operation}, {candidate.Key},
                    {candidate.RequestFingerprint}, {candidate.CreatedAt}, {candidate.ExpiresAt})
                ON CONFLICT (principal_id, operation, key) DO NOTHING
                """, cancellationToken);
            var record = await db.IdempotencyRecords.SingleAsync(x => x.PrincipalId == candidate.PrincipalId
                && x.Operation == candidate.Operation && x.Key == candidate.Key, cancellationToken);
            return new AssignmentReservation(inserted == 1, record);
        });

        public Task<AssignmentReservation> ReserveUnassignAsync(Guid actor, UnassignCourierCommand command, DateTimeOffset now,
            CancellationToken cancellationToken) => Protect(async () =>
        {
            var candidate = new IdempotencyRecord(Guid.NewGuid(), actor.ToString("D"), AssignmentValidator.UnassignOperation,
                command.Key, command.Fingerprint, now);
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO idempotency_keys (id, principal_id, operation, key, request_fingerprint, created_at, expires_at)
                VALUES ({candidate.Id}, {candidate.PrincipalId}, {candidate.Operation}, {candidate.Key},
                    {candidate.RequestFingerprint}, {candidate.CreatedAt}, {candidate.ExpiresAt})
                ON CONFLICT (principal_id, operation, key) DO NOTHING
                """, cancellationToken);
            var record = await db.IdempotencyRecords.SingleAsync(x => x.PrincipalId == candidate.PrincipalId
                && x.Operation == candidate.Operation && x.Key == candidate.Key, cancellationToken);
            return new AssignmentReservation(inserted == 1, record);
        });

        public Task<CourierAssignment?> LockCurrentAssignmentAsync(Guid parcelId, CancellationToken cancellationToken) => Protect<CourierAssignment?>(async () =>
        {
            var rows = await db.Assignments.FromSqlInterpolated($"""
                SELECT *, xmin FROM assignments WHERE parcel_id = {parcelId} AND status = 'Active' FOR UPDATE
                """).ToListAsync(cancellationToken);
            return rows.SingleOrDefault();
        });

        public async Task RetireAsync(CourierAssignment assignment, CancellationToken cancellationToken)
        {
            await Protect(async () =>
            {
                if (db.Database.CurrentTransaction != transaction || db.Entry(assignment).State == EntityState.Detached)
                    throw new InvalidOperationException("Retirement must belong to the owning transaction.");
                assignment.MarkReassigned();
                return await db.SaveChangesAsync(cancellationToken);
            });
        }

        public async Task CancelAssignmentAsync(CourierAssignment assignment, CancellationToken cancellationToken)
        {
            await Protect(async () =>
            {
                if (db.Database.CurrentTransaction != transaction || db.Entry(assignment).State == EntityState.Detached)
                    throw new InvalidOperationException("Cancellation must belong to the owning transaction.");
                assignment.MarkCancelled();
                return await db.SaveChangesAsync(cancellationToken);
            });
        }

        public async Task CheckEligibilityAsync(Parcel parcel, Guid courierId, DateOnly date, DateOnly today, CancellationToken cancellationToken, Guid? originalCourierId = null)
        {
            await Protect(async () =>
            {
                if (originalCourierId is null && await db.Assignments.AnyAsync(x => x.ParcelId == parcel.Id && x.Status == AssignmentStatus.Active, cancellationToken))
                    throw new AssignmentException(AssignmentFailure.InvalidTransition);
                // Every allocation writer takes parcel first, then courier. The courier row
                // serializes counts across different parcels; count is a fresh READ COMMITTED statement.
                var previousCourierId = originalCourierId ?? courierId;
                // PostgreSQL uuid order is the allocation lock order, including opposite-direction swaps.
                var rows = await db.Couriers.FromSqlInterpolated($"""
                    SELECT * FROM couriers WHERE id = {courierId} OR id = {previousCourierId} ORDER BY id FOR UPDATE
                    """).ToListAsync(cancellationToken);
                if (rows.SingleOrDefault(x => x.Id == courierId) is not { } courier) throw new AssignmentException(AssignmentFailure.NotFound);
                if (!courier.Active || (date == today && !courier.OnDuty)) throw new AssignmentException(AssignmentFailure.CourierUnavailable);
                var memberships = await db.CourierZones.FromSqlInterpolated($"""
                    SELECT * FROM courier_zones WHERE courier_id = {courierId} AND zone_id = {parcel.ZoneId} FOR SHARE
                    """).AsNoTracking().ToListAsync(cancellationToken);
                if (memberships.Count == 0) throw new AssignmentException(AssignmentFailure.ZoneMismatch);
                var count = await db.Assignments.CountAsync(x => x.CourierId == courierId && x.ServiceDate == date
                    && x.Status == AssignmentStatus.Active, cancellationToken);
                if (count >= courier.Capacity) throw new AssignmentException(AssignmentFailure.CapacityExceeded);
                if (parcel.WeightKg > courier.MaxWeightKg) throw new AssignmentException(AssignmentFailure.WeightCapacityExceeded);
                return true;
            });
        }

        public async Task SaveAsync(AssignmentState state, CourierAssignment assignment, BookingHistory history,
            OutboxMessage outbox, IdempotencyRecord record, CancellationToken cancellationToken)
        {
            await Protect(async () =>
            {
                if (db.Database.CurrentTransaction != transaction || db.Entry(state.Parcel).State == EntityState.Detached
                    || db.Entry(record).State == EntityState.Detached || assignment.ParcelId != state.Parcel.Id
                    || history.ParcelId != state.Parcel.Id || record.ParcelId != state.Parcel.Id)
                    throw new InvalidOperationException("Assignment records must share the owning transaction.");
                db.Assignments.Add(assignment);
                db.BookingHistory.Add(history);
                db.OutboxMessages.Add(outbox);
                return await db.SaveChangesAsync(cancellationToken);
            });
        }

        public async Task SaveUnassignmentAsync(AssignmentState state, BookingHistory history,
            OutboxMessage outbox, IdempotencyRecord record, CancellationToken cancellationToken)
        {
            await Protect(async () =>
            {
                if (db.Database.CurrentTransaction != transaction || db.Entry(state.Parcel).State == EntityState.Detached
                    || db.Entry(record).State == EntityState.Detached
                    || history.ParcelId != state.Parcel.Id || record.ParcelId != state.Parcel.Id)
                    throw new InvalidOperationException("Unassignment records must share the owning transaction.");
                db.BookingHistory.Add(history);
                db.OutboxMessages.Add(outbox);
                return await db.SaveChangesAsync(cancellationToken);
            });
        }
        public async Task CommitAsync(CancellationToken cancellationToken)
            => await Protect(async () => { await transaction.CommitAsync(cancellationToken); return true; });
        public async ValueTask DisposeAsync()
        {
            try { await Protect(async () => { await transaction.DisposeAsync(); return true; }); }
            finally { db.ChangeTracker.Clear(); }
        }
    }

    private static async Task<T> Protect<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception exception) when (exception is NpgsqlException or DbUpdateException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            throw new AssignmentException(DeliveryDatabaseErrors.Classify(exception) == DeliveryDatabaseFailure.ConcurrentWrite
                ? AssignmentFailure.OperationInProgress : AssignmentFailure.Unavailable, inner: exception);
        }
    }
}
