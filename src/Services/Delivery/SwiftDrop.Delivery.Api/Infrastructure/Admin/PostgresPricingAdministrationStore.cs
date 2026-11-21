using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.Admin.Pricing;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.Admin;

public sealed class PostgresPricingAdministrationStore(Func<DeliveryDbContext> database) : IPricingAdministrationStore
{
    public async Task<PricingOverviewPage> ListOverviewAsync(int page, int pageSize, Guid? zoneId, CancellationToken cancellationToken)
    {
        await using var db = database();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var query = db.Zones.AsNoTracking().AsQueryable();
        if (zoneId.HasValue) query = query.Where(zone => zone.Id == zoneId.Value);
        var total = await query.LongCountAsync(cancellationToken);
        var zones = await query.OrderBy(zone => zone.Name).ThenBy(zone => zone.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var ids = zones.Select(zone => zone.Id).ToArray();
        var counts = await db.PricingTiers.AsNoTracking().Where(tier => ids.Contains(tier.ZoneId))
            .GroupBy(tier => tier.ZoneId).Select(group => new { ZoneId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.ZoneId, group => group.Count, cancellationToken);
        var active = await db.PricingTiers.AsNoTracking().Where(tier => ids.Contains(tier.ZoneId) && tier.IsActive)
            .OrderBy(tier => tier.Size).Select(tier => new { Tier = tier, Version = EF.Property<uint>(tier, "xmin") })
            .ToListAsync(cancellationToken);
        var rows = zones.Select(zone => new PricingZoneOverview(zone.Id, zone.Name, zone.IsActive,
            counts.GetValueOrDefault(zone.Id), active.Where(row => row.Tier.ZoneId == zone.Id).Select(row =>
                new PricingTierDto(row.Tier.Id, zone.Id, zone.Name, row.Tier.Size.ToString(), row.Tier.PricingVersion,
                    row.Tier.BaseRate, row.Tier.PerKgRate, row.Tier.Currency, row.Tier.IsActive, row.Version)).ToList())).ToList();
        await transaction.CommitAsync(cancellationToken);
        return new(rows, total, page, pageSize);
    }

    public async Task<PricingPage> ListPricingAsync(int page, int pageSize, Guid? zoneId, CancellationToken cancellationToken)
    {
        await using var db = database();
        var query = db.PricingTiers.AsNoTracking();

        if (zoneId.HasValue && zoneId.Value != Guid.Empty)
        {
            query = query.Where(p => p.ZoneId == zoneId.Value);
        }

        var total = await query.LongCountAsync(cancellationToken);

        var zonesMap = await db.Zones.AsNoTracking().ToDictionaryAsync(z => z.Id, z => z.Name, cancellationToken);

        var rawTiers = await query
            .OrderBy(p => p.ZoneId)
            .ThenBy(p => p.Size)
            .ThenBy(p => p.PricingVersion)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(tier => new { Tier = tier, Version = EF.Property<uint>(tier, "xmin") })
            .ToListAsync(cancellationToken);

        var dtos = rawTiers.Select(row =>
        {
            var t = row.Tier;
            var zoneName = zonesMap.TryGetValue(t.ZoneId, out var name) ? name : "Unknown Zone";
            return new PricingTierDto(t.Id, t.ZoneId, zoneName, t.Size.ToString(), t.PricingVersion,
                t.BaseRate, t.PerKgRate, t.Currency, t.IsActive, row.Version);
        }).ToList();

        return new PricingPage(dtos, total, page, pageSize);
    }

    public async Task<PricingResult<PricingTierDto>> CreatePricingTierAsync(PricingTierInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = database();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(894453120)", cancellationToken);

        // 1. Verify zone exists
        var zone = await db.Zones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == input.ZoneId, cancellationToken);
        if (zone is null)
        {
            return new(null, PricingFailure.InvalidInput,
                new Dictionary<string, string[]> { ["zoneId"] = ["The specified delivery zone does not exist."] });
        }

        var parsedSize = Enum.Parse<ParcelSize>(input.Size, ignoreCase: true);
        var trimmedVersion = input.PricingVersion.Trim();

        // 2. Check unique (ZoneId, Size, PricingVersion)
        var exists = await db.PricingTiers.AnyAsync(p =>
            p.ZoneId == input.ZoneId && p.Size == parsedSize && p.PricingVersion == trimmedVersion, cancellationToken);

        if (exists)
        {
            return new(null, PricingFailure.Conflict,
                new Dictionary<string, string[]>
                {
                    ["pricingVersion"] = [$"A pricing tier for zone '{zone.Name}', size '{parsedSize}' and version '{trimmedVersion}' already exists."]
                });
        }

        if (input.IsActive)
        {
            var previous = await db.PricingTiers.Where(t => t.ZoneId == input.ZoneId && t.Size == parsedSize && t.IsActive).ToListAsync(cancellationToken);
            foreach (var old in previous) old.SetActive(false);
            await db.SaveChangesAsync(cancellationToken);
        }

        var tierId = Guid.NewGuid();
        var tier = new PricingTier(tierId, input.ZoneId, parsedSize, trimmedVersion,
            input.BaseRate, input.PerKgRate, input.Currency);

        if (!input.IsActive)
        {
            tier.SetActive(false);
        }

        var audit = new DeliveryConfigAudit(Guid.NewGuid(), actorId, "CreatePricingTier", "PricingTier",
            tierId, JsonSerializer.Serialize(new
            {
                tier.ZoneId,
                ZoneName = zone.Name,
                Size = tier.Size.ToString(),
                tier.PricingVersion,
                tier.BaseRate,
                tier.PerKgRate,
                tier.Currency,
                tier.IsActive
            }), now);

        db.PricingTiers.Add(tier);
        db.ConfigAuditLogs.Add(audit);
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new(new PricingTierDto(tier.Id, tier.ZoneId, zone.Name, tier.Size.ToString(),
            tier.PricingVersion, tier.BaseRate, tier.PerKgRate, tier.Currency, tier.IsActive, db.Entry(tier).Property<uint>("xmin").CurrentValue));
    }

    public async Task<PricingResult<PricingTierDto>> UpdatePricingTierAsync(Guid id, PricingTierUpdateInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = database();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(894453120)", cancellationToken);
        var tier = await db.PricingTiers.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (tier is null)
        {
            return new(null, PricingFailure.NotFound);
        }

        if (input.ExpectedVersion is not > 0)
            return new(null, PricingFailure.InvalidInput, new Dictionary<string, string[]> { ["expectedVersion"] = ["The current tier version is required."] });
        if (input.ExpectedVersion != db.Entry(tier).Property<uint>("xmin").CurrentValue)
            return new(null, PricingFailure.Conflict);
        if (input.IsActive)
        {
            var previous = await db.PricingTiers.Where(t => t.Id != id && t.ZoneId == tier.ZoneId && t.Size == tier.Size && t.IsActive).ToListAsync(cancellationToken);
            foreach (var old in previous) old.SetActive(false);
            await db.SaveChangesAsync(cancellationToken);
        }

        var zone = await db.Zones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == tier.ZoneId, cancellationToken);
        var zoneName = zone?.Name ?? "Unknown Zone";

        tier.SetActive(input.IsActive);

        var audit = new DeliveryConfigAudit(Guid.NewGuid(), actorId, "UpdatePricingTierStatus", "PricingTier",
            tier.Id, JsonSerializer.Serialize(new
            {
                tier.ZoneId,
                ZoneName = zoneName,
                Size = tier.Size.ToString(),
                tier.PricingVersion,
                tier.IsActive
            }), now);

        db.ConfigAuditLogs.Add(audit);
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new(new PricingTierDto(tier.Id, tier.ZoneId, zoneName, tier.Size.ToString(),
            tier.PricingVersion, tier.BaseRate, tier.PerKgRate, tier.Currency, tier.IsActive, db.Entry(tier).Property<uint>("xmin").CurrentValue));
    }
}
