using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.Admin.Zones;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.Admin;

public sealed class PostgresZoneAdministrationStore(Func<DeliveryDbContext> database) : IZoneAdministrationStore
{
    public async Task<ZonePage> ListZonesAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var db = database();
        var query = db.Zones.AsNoTracking().OrderBy(z => z.Name).ThenBy(z => z.Id);
        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(zone => new { Zone = zone, Version = EF.Property<uint>(zone, "xmin") }).ToListAsync(cancellationToken);

        var dtos = rows.Select(row =>
        {
            var z = row.Zone;
            var (city, postalCodes) = ZoneBoundaryHelper.DeserializeBoundaries(z.Boundaries);
            return new ZoneDto(z.Id, z.Name, city, postalCodes, z.IsActive, row.Version);
        }).ToList();

        return new ZonePage(dtos, total, page, pageSize);
    }

    public async Task<ZoneDto?> GetZoneByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = database();
        var zone = await db.Zones.FirstOrDefaultAsync(z => z.Id == id, cancellationToken);
        if (zone is null)
        {
            return null;
        }

        var (city, postalCodes) = ZoneBoundaryHelper.DeserializeBoundaries(zone.Boundaries);
        return new ZoneDto(zone.Id, zone.Name, city, postalCodes, zone.IsActive, db.Entry(zone).Property<uint>("xmin").CurrentValue);
    }

    public async Task<ZoneResult<ZoneDto>> CreateZoneAsync(ZoneInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = database();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(894453120)", cancellationToken);

        // 1. Check unique name
        var trimmedName = input.Name.Trim();
        var nameExists = await db.Zones.AnyAsync(z => z.Name.ToLower() == trimmedName.ToLower(), cancellationToken);
        if (nameExists)
        {
            return new(null, ZoneFailure.Conflict,
                new Dictionary<string, string[]> { ["name"] = [$"Zone with name '{trimmedName}' already exists."] });
        }

        // 2. Validate postal code overlap across active zones
        var boundariesJson = ZoneBoundaryHelper.SerializeBoundaries(input.City, input.ServicePostalCodes);
        var (_, requestedPostalCodes) = ZoneBoundaryHelper.DeserializeBoundaries(boundariesJson);

        if (input.IsActive && requestedPostalCodes.Length > 0)
        {
            var activeZones = await db.Zones.AsNoTracking()
                .Where(z => z.IsActive && z.Boundaries != null)
                .ToListAsync(cancellationToken);

            foreach (var existing in activeZones)
            {
                var (_, existingCodes) = ZoneBoundaryHelper.DeserializeBoundaries(existing.Boundaries);
                var overlap = requestedPostalCodes.Intersect(existingCodes, StringComparer.OrdinalIgnoreCase).ToArray();
                if (overlap.Length > 0)
                {
                    return new(null, ZoneFailure.OverlappingPostalCodes,
                        new Dictionary<string, string[]>
                        {
                            ["servicePostalCodes"] = [$"Postal code(s) [{string.Join(", ", overlap)}] already assigned to active zone '{existing.Name}'."]
                        });
                }
            }
        }

        var zoneId = Guid.NewGuid();
        var zone = new DeliveryZone(zoneId, trimmedName, input.City, boundariesJson);
        if (!input.IsActive)
        {
            zone.SetActive(false);
        }

        var audit = new DeliveryConfigAudit(Guid.NewGuid(), actorId, "CreateZone", "DeliveryZone",
            zoneId, JsonSerializer.Serialize(new { zone.Name, input.City, requestedPostalCodes, zone.IsActive }), now);

        db.Zones.Add(zone);
        db.ConfigAuditLogs.Add(audit);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(new ZoneDto(zone.Id, zone.Name, input.City, requestedPostalCodes, zone.IsActive, db.Entry(zone).Property<uint>("xmin").CurrentValue));
    }

    public async Task<ZoneResult<ZoneDto>> UpdateZoneAsync(Guid id, ZoneInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = database();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(894453120)", cancellationToken);
        var zone = await db.Zones.FirstOrDefaultAsync(z => z.Id == id, cancellationToken);
        if (zone is null)
        {
            return new(null, ZoneFailure.NotFound);
        }

        if (input.ExpectedVersion is not > 0)
            return new(null, ZoneFailure.InvalidInput, new Dictionary<string, string[]> { ["expectedVersion"] = ["The current zone version is required."] });
        if (input.ExpectedVersion != db.Entry(zone).Property<uint>("xmin").CurrentValue)
            return new(null, ZoneFailure.Conflict);

        var trimmedName = input.Name.Trim();
        var nameConflict = await db.Zones.AnyAsync(z => z.Id != id && z.Name.ToLower() == trimmedName.ToLower(), cancellationToken);
        if (nameConflict)
        {
            return new(null, ZoneFailure.Conflict,
                new Dictionary<string, string[]> { ["name"] = [$"Zone with name '{trimmedName}' already exists."] });
        }

        var boundariesJson = ZoneBoundaryHelper.SerializeBoundaries(input.City, input.ServicePostalCodes);
        var (_, requestedPostalCodes) = ZoneBoundaryHelper.DeserializeBoundaries(boundariesJson);

        if (input.IsActive && requestedPostalCodes.Length > 0)
        {
            var activeZones = await db.Zones.AsNoTracking()
                .Where(z => z.IsActive && z.Id != id && z.Boundaries != null)
                .ToListAsync(cancellationToken);

            foreach (var existing in activeZones)
            {
                var (_, existingCodes) = ZoneBoundaryHelper.DeserializeBoundaries(existing.Boundaries);
                var overlap = requestedPostalCodes.Intersect(existingCodes, StringComparer.OrdinalIgnoreCase).ToArray();
                if (overlap.Length > 0)
                {
                    return new(null, ZoneFailure.OverlappingPostalCodes,
                        new Dictionary<string, string[]>
                        {
                            ["servicePostalCodes"] = [$"Postal code(s) [{string.Join(", ", overlap)}] already assigned to active zone '{existing.Name}'."]
                        });
                }
            }
        }

        zone.Update(trimmedName, input.City, boundariesJson, input.IsActive);

        var audit = new DeliveryConfigAudit(Guid.NewGuid(), actorId, "UpdateZone", "DeliveryZone",
            zone.Id, JsonSerializer.Serialize(new { zone.Name, input.City, requestedPostalCodes, zone.IsActive }), now);

        db.ConfigAuditLogs.Add(audit);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(new ZoneDto(zone.Id, zone.Name, input.City, requestedPostalCodes, zone.IsActive, db.Entry(zone).Property<uint>("xmin").CurrentValue));
    }

    public async Task<ZoneSummaryResponse> GetZoneSummaryAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        await using var db = database();
        var allZones = await db.Zones.AsNoTracking().OrderBy(z => z.Name).ToListAsync(cancellationToken);

        var query = db.Parcels.AsNoTracking();
        if (from.HasValue)
        {
            query = query.Where(p => p.CreatedAt >= from.Value);
        }
        if (to.HasValue)
        {
            query = query.Where(p => p.CreatedAt < to.Value);
        }

        var parcelsByZone = await query
            .GroupBy(p => new { p.ZoneId, p.Status })
            .Select(g => new { g.Key.ZoneId, Status = g.Key.Status.ToString(), Count = g.Count() })
            .ToListAsync(cancellationToken);

        var summaryItems = allZones.Select(zone =>
        {
            var zoneParcels = parcelsByZone.Where(p => p.ZoneId == zone.Id).ToList();
            var counts = zoneParcels.ToDictionary(k => k.Status, v => v.Count);
            var total = zoneParcels.Sum(p => p.Count);
            return new ZoneSummaryItemDto(zone.Id, zone.Name, counts, total);
        }).ToList();

        return new ZoneSummaryResponse(summaryItems);
    }
}
