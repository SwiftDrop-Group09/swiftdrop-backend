using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence;

public sealed class DeliveryDbContext(DbContextOptions<DeliveryDbContext> options) : DbContext(options)
{
    public DbSet<MerchantBookingIdentity> MerchantBookingIdentities => Set<MerchantBookingIdentity>();
    public DbSet<DeliveryZone> Zones => Set<DeliveryZone>();
    public DbSet<PricingTier> PricingTiers => Set<PricingTier>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<Parcel> Parcels => Set<Parcel>();
    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();
    public DbSet<BookingHistory> BookingHistory => Set<BookingHistory>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Courier> Couriers => Set<Courier>();
    public DbSet<CourierZone> CourierZones => Set<CourierZone>();
    public DbSet<CourierAssignment> Assignments => Set<CourierAssignment>();
    public DbSet<ProofMetadata> ProofMetadata => Set<ProofMetadata>();
    public DbSet<DeliveryConfigAudit> ConfigAuditLogs => Set<DeliveryConfigAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        DeliveryEntityConfigurations.Configure(modelBuilder);
        var identity = modelBuilder.Entity<MerchantBookingIdentity>();
        identity.ToTable("merchant_booking_identities");
        identity.HasKey(row => new { row.MerchantId, row.ExternalOrderId });
        identity.Property(row => row.MerchantId).HasColumnName("merchant_id");
        identity.Property(row => row.ExternalOrderId).HasColumnName("external_order_id").HasMaxLength(128);
        identity.Property(row => row.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
        identity.Property(row => row.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64);
        identity.Property(row => row.ParcelId).HasColumnName("parcel_id");
        identity.HasIndex(row => new { row.MerchantId, row.IdempotencyKey }).IsUnique();
        identity.HasOne<Parcel>().WithMany().HasForeignKey(row => row.ParcelId).OnDelete(DeleteBehavior.Restrict);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardImmutableRecords();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardImmutableRecords();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardImmutableRecords()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries<DeliveryConfigAudit>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Delivery configuration audit records are append-only.");
        if (ChangeTracker.Entries<PriceSnapshot>().Any(entry =>
            entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Accepted price snapshots are immutable.");
        }

        if (ChangeTracker.Entries<BookingHistory>().Any(entry =>
            entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Parcel status history is append-only.");
        }

        if (ChangeTracker.Entries<ProofMetadata>().Any(entry =>
            entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Committed proof metadata is retained and immutable.");
        }
    }
}
