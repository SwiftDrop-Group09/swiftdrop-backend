using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.Quotes;

public sealed class PostgresQuoteStore(Func<DeliveryDbContext> contextFactory) : IQuoteStore
{
    public async Task<QuotePricingData> ReadPricingAsync(Guid pickupZoneId, Guid deliveryZoneId,
        ParcelSize size, CancellationToken cancellationToken)
    {
        try
        {
            var database = contextFactory();
            // Read zone eligibility and tariffs from a consistent configuration
            // snapshot. Retained tariff economic fields are immutable afterwards.
            await using var transaction = await database.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken);
            var activeZones = await database.Zones.AsNoTracking()
                .Where(zone => zone.IsActive && (zone.Id == pickupZoneId || zone.Id == deliveryZoneId))
                .Select(zone => zone.Id).ToArrayAsync(cancellationToken);
            var pickupActive = activeZones.Contains(pickupZoneId);
            var deliveryActive = activeZones.Contains(deliveryZoneId);
            var tiers = pickupActive && deliveryActive
                ? await database.PricingTiers.AsNoTracking()
                    .Where(tier => tier.IsActive && tier.ZoneId == deliveryZoneId && tier.Size == size)
                    .Take(2).ToArrayAsync(cancellationToken)
                : [];
            await transaction.CommitAsync(cancellationToken);
            return new QuotePricingData(pickupActive, deliveryActive, tiers);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw new QuoteStoreUnavailableException("Delivery quote storage is unavailable.", exception);
        }
    }

    public async Task SaveAsync(Quote quote, CancellationToken cancellationToken)
    {
        DeliveryDbContext? database = null;
        try
        {
            database = contextFactory();
            database.Quotes.Add(quote);
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            if (database is not null)
            {
                database.Entry(quote).State = EntityState.Detached;
            }

            throw new QuoteStoreUnavailableException("Delivery quote storage is unavailable.", exception);
        }
    }

    private static bool IsStorageFailure(Exception exception) => exception is
        NpgsqlException or DbUpdateException or TimeoutException or InvalidOperationException or ArgumentException;
}
