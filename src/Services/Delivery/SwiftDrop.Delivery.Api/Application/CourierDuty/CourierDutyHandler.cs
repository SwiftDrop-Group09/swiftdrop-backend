using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Application.CourierDuty;

public sealed record CourierDutyStatusResponse(
    Guid CourierId,
    bool Active,
    bool OnDuty,
    int Capacity,
    string VehicleType,
    string? VehicleNumber,
    decimal MaxWeightKg,
    string CourierCode,
    IReadOnlyList<Guid> ZoneIds);

public sealed record SetCourierDutyInput(bool OnDuty);

public sealed class CourierDutyHandler(Func<DeliveryDbContext> contextFactory, ICurrentDeliveryUser actor)
{
    public async Task<CourierDutyStatusResponse?> GetDutyStatusAsync(CancellationToken cancellationToken)
    {
        if (!actor.IsAuthenticated || actor.UserId is not { } userId || userId == Guid.Empty || !actor.IsCourier)
        {
            return null;
        }

        var db = contextFactory();
        var courier = await db.Couriers.SingleOrDefaultAsync(c => c.Id == userId, cancellationToken);
        if (courier is null)
        {
            // Auto-provision if identity user has courier role but operational profile not yet created
            courier = new Courier(userId, active: true, onDuty: false, capacity: 10,
                VehicleType.Motorcycle, null, 30.00m);
            db.Couriers.Add(courier);
            await db.SaveChangesAsync(cancellationToken);
        }

        var zoneIds = await db.CourierZones.AsNoTracking()
            .Where(z => z.CourierId == userId)
            .Select(z => z.ZoneId)
            .ToListAsync(cancellationToken);

        return ToResponse(courier, zoneIds);
    }

    public async Task<CourierDutyStatusResponse?> SetDutyAsync(bool onDuty, CancellationToken cancellationToken)
    {
        if (!actor.IsAuthenticated || actor.UserId is not { } userId || userId == Guid.Empty || !actor.IsCourier)
        {
            return null;
        }

        var db = contextFactory();
        var courier = await db.Couriers.SingleOrDefaultAsync(c => c.Id == userId, cancellationToken);
        if (courier is null)
        {
            courier = new Courier(userId, active: true, onDuty: onDuty, capacity: 10,
                VehicleType.Motorcycle, null, 30.00m);
            db.Couriers.Add(courier);
        }
        else
        {
            courier.SetDuty(onDuty);
        }

        await db.SaveChangesAsync(cancellationToken);

        var zoneIds = await db.CourierZones.AsNoTracking()
            .Where(z => z.CourierId == userId)
            .Select(z => z.ZoneId)
            .ToListAsync(cancellationToken);

        return ToResponse(courier, zoneIds);
    }

    private static CourierDutyStatusResponse ToResponse(Courier courier, IReadOnlyList<Guid> zoneIds) =>
        new(
            courier.Id,
            courier.Active,
            courier.OnDuty,
            courier.Capacity,
            courier.VehicleType.ToString(),
            courier.VehicleNumber,
            courier.MaxWeightKg,
            "COU-" + courier.BusinessNumber.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
            zoneIds);
}

