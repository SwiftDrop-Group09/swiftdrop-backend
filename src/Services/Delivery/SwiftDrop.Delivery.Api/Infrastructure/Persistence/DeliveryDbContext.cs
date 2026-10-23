using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence;

public sealed class DeliveryDbContext(DbContextOptions<DeliveryDbContext> options) : DbContext(options)
{
    public DbSet<DeliveryZone> Zones => Set<DeliveryZone>();
    public DbSet<PricingTier> PricingTiers => Set<PricingTier>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<Parcel> Parcels => Set<Parcel>();
    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();
    public DbSet<BookingHistory> BookingHistory => Set<BookingHistory>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        DeliveryEntityConfigurations.Configure(modelBuilder);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardImmutableSnapshots();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardImmutableSnapshots();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardImmutableSnapshots()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries<PriceSnapshot>().Any(entry =>
            entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Accepted price snapshots are immutable.");
        }
    }
}
