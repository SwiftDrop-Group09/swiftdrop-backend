using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Application.Dispatch;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.Dispatch;

public sealed class PostgresWorkloadReadStore(Func<DeliveryDbContext> contextFactory) : IWorkloadReadStore
{
    public async Task<WorkloadPage> GetWorkloadAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        try
        {
            var db = contextFactory();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var total = await db.Couriers.CountAsync(cancellationToken);
            var rows = await db.Couriers.AsNoTracking().OrderBy(courier => courier.Id)
                .Skip(checked((page - 1) * pageSize)).Take(pageSize)
                .Select(courier => new
                {
                    courier.Id, courier.Active, courier.OnDuty, courier.Capacity,
                    ActiveCount = db.Assignments.Count(assignment => assignment.CourierId == courier.Id
                        && assignment.Status == AssignmentStatus.Active)
                }).ToArrayAsync(cancellationToken);
            var ids = rows.Select(row => row.Id).ToArray();
            var memberships = await db.CourierZones.AsNoTracking().Where(zone => ids.Contains(zone.CourierId))
                .OrderBy(zone => zone.ZoneId).ToArrayAsync(cancellationToken);
            var zones = memberships.ToLookup(zone => zone.CourierId, zone => zone.ZoneId);
            var items = rows.Select(row => new CourierWorkloadResponse(row.Id, row.Active, row.OnDuty,
                row.ActiveCount, row.Capacity, zones[row.Id].ToArray())).ToArray();
            await transaction.CommitAsync(cancellationToken);
            return new(items, total, page, pageSize);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw new DeliveryReadUnavailableException("Courier workload reads are unavailable.", exception);
        }
    }

    public async Task<DispatchAssignmentsPage?> GetAssignmentsAsync(Guid courierId, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        try
        {
            var db = contextFactory();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            if (!await db.Couriers.AnyAsync(courier => courier.Id == courierId, cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            // Outstanding work spans every service date, including overdue and future bookings.
            var assignments = db.Assignments.AsNoTracking().Where(assignment => assignment.CourierId == courierId
                && assignment.Status == AssignmentStatus.Active);
            var total = await assignments.CountAsync(cancellationToken);
            var rows = await (from assignment in assignments
                              join parcel in db.Parcels.AsNoTracking() on assignment.ParcelId equals parcel.Id
                              orderby assignment.AssignedAt descending, assignment.Id descending
                              select new { parcel.Id, parcel.TrackingNumber, parcel.Status,
                                  assignment.ServiceDate, assignment.AssignedAt })
                .Skip(checked((page - 1) * pageSize)).Take(pageSize).ToArrayAsync(cancellationToken);
            var items = rows.Select(row => new DispatchAssignmentResponse(row.Id, row.TrackingNumber,
                row.Status.ToString(), row.ServiceDate, row.AssignedAt)).ToArray();
            await transaction.CommitAsync(cancellationToken);
            return new(courierId, items, total, page, pageSize);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw new DeliveryReadUnavailableException("Courier assignments are unavailable.", exception);
        }
    }

    private static bool IsReadFailure(Exception exception) => exception is
        NpgsqlException or DbUpdateException or TimeoutException or InvalidOperationException or ArgumentException;
}
