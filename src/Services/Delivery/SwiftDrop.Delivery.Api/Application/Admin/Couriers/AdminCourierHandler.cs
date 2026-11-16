using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Application.Admin.Couriers;

public sealed record AdminCourierItem(
    Guid CourierId,
    string CourierCode,
    bool Active,
    bool OnDuty,
    int Capacity,
    string VehicleType,
    string? VehicleNumber,
    decimal MaxWeightKg,
    IReadOnlyList<Guid> ZoneIds,
    int ActiveDeliveriesCount);

public sealed record UpdateCourierProfileInput(
    bool Active,
    bool OnDuty,
    int Capacity,
    string VehicleType,
    string? VehicleNumber,
    decimal MaxWeightKg,
    IReadOnlyList<Guid>? ZoneIds);

public sealed class AdminCourierHandler(Func<DeliveryDbContext> contextFactory)
{
    public async Task<IReadOnlyList<AdminCourierItem>> ListCouriersAsync(CancellationToken cancellationToken)
    {
        var db = contextFactory();
        var couriers = await db.Couriers.AsNoTracking().OrderBy(c => c.BusinessNumber).ToListAsync(cancellationToken);
        var courierIds = couriers.Select(c => c.Id).ToArray();

        var zones = await db.CourierZones.AsNoTracking()
            .Where(z => courierIds.Contains(z.CourierId))
            .ToListAsync(cancellationToken);
        var zonesByCourier = zones.ToLookup(z => z.CourierId, z => z.ZoneId);

        var activeCounts = await db.Assignments.AsNoTracking()
            .Where(a => courierIds.Contains(a.CourierId) && a.Status == AssignmentStatus.Active)
            .GroupBy(a => a.CourierId)
            .Select(g => new { CourierId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CourierId, x => x.Count, cancellationToken);

        return couriers.Select(c => new AdminCourierItem(
            c.Id,
            "COU-" + c.BusinessNumber.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
            c.Active,
            c.OnDuty,
            c.Capacity,
            c.VehicleType.ToString(),
            c.VehicleNumber,
            c.MaxWeightKg,
            zonesByCourier[c.Id].ToList(),
            activeCounts.TryGetValue(c.Id, out var count) ? count : 0)).ToList();
    }

    public async Task<AdminCourierItem?> GetCourierAsync(Guid id, CancellationToken cancellationToken)
    {
        var db = contextFactory();
        var courier = await db.Couriers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (courier is null) return null;

        var zones = await db.CourierZones.AsNoTracking()
            .Where(z => z.CourierId == id)
            .Select(z => z.ZoneId)
            .ToListAsync(cancellationToken);

        var activeCount = await db.Assignments.AsNoTracking()
            .CountAsync(a => a.CourierId == id && a.Status == AssignmentStatus.Active, cancellationToken);

        return new AdminCourierItem(
            courier.Id,
            "COU-" + courier.BusinessNumber.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
            courier.Active,
            courier.OnDuty,
            courier.Capacity,
            courier.VehicleType.ToString(),
            courier.VehicleNumber,
            courier.MaxWeightKg,
            zones,
            activeCount);
    }

    public async Task<AdminCourierItem?> UpdateCourierAsync(Guid id, UpdateCourierProfileInput input, CancellationToken cancellationToken)
    {
        if (input.Capacity < 1 || input.MaxWeightKg <= 0) return null;
        if (!Enum.TryParse<VehicleType>(input.VehicleType, ignoreCase: true, out var vehicleType))
        {
            vehicleType = VehicleType.Motorcycle;
        }

        var db = contextFactory();
        var courier = await db.Couriers.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (courier is null)
        {
            courier = new Courier(id, input.Active, input.OnDuty, input.Capacity, vehicleType, input.VehicleNumber, input.MaxWeightKg);
            db.Couriers.Add(courier);
        }
        else
        {
            courier.Configure(input.Active, input.OnDuty, input.Capacity, vehicleType, input.VehicleNumber, input.MaxWeightKg);
        }

        if (input.ZoneIds is not null)
        {
            var existingZones = await db.CourierZones.Where(z => z.CourierId == id).ToListAsync(cancellationToken);
            var targetZoneIds = input.ZoneIds.ToHashSet();

            // Remove unselected
            var toRemove = existingZones.Where(z => !targetZoneIds.Contains(z.ZoneId)).ToList();
            db.CourierZones.RemoveRange(toRemove);

            // Add newly selected
            var existingZoneIds = existingZones.Select(z => z.ZoneId).ToHashSet();
            foreach (var zoneId in targetZoneIds)
            {
                if (!existingZoneIds.Contains(zoneId))
                {
                    db.CourierZones.Add(new CourierZone(id, zoneId));
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return await GetCourierAsync(id, cancellationToken);
    }
}

