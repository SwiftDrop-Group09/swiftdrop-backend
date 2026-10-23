using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence;

internal static partial class DeliveryEntityConfigurations
{
    private const string StatusValues = "'Created','Assigned','PickedUp','InTransit','OutForDelivery','Delivered','Failed','Cancelled'";
    private const string SizeValues = "'Small','Medium','Large'";

    public static void Configure(ModelBuilder model)
    {
        var zones = model.Entity<DeliveryZone>();
        zones.ToTable("delivery_zones", table => table.HasCheckConstraint("ck_zones_name", "btrim(name) <> ''"));
        zones.HasKey(x => x.Id);
        zones.HasIndex(x => x.Name).IsUnique();
        zones.Property(x => x.Name).IsRequired();
        zones.Property(x => x.Boundaries).HasColumnType("jsonb");

        var tiers = model.Entity<PricingTier>();
        tiers.ToTable("pricing_tiers", table =>
        {
            table.HasCheckConstraint("ck_pricing_size", $"size_category IN ({SizeValues})");
            table.HasCheckConstraint("ck_pricing_amounts", "base_rate >= 0 AND per_kg_rate >= 0");
            table.HasCheckConstraint("ck_pricing_currency", "currency = 'LKR'");
            table.HasCheckConstraint("ck_pricing_version", "btrim(pricing_version) <> ''");
        });
        tiers.HasKey(x => x.Id);
        tiers.HasAlternateKey(x => new { x.Id, x.PricingVersion, x.Currency });
        tiers.HasAlternateKey(x => new { x.Id, x.ZoneId, x.PricingVersion, x.Currency });
        tiers.HasIndex(x => new { x.ZoneId, x.Size, x.PricingVersion }).IsUnique();
        tiers.HasOne<DeliveryZone>().WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        tiers.Property(x => x.Size).HasConversion<string>();
        tiers.Property(x => x.BaseRate).HasPrecision(18, 2);
        tiers.Property(x => x.PerKgRate).HasPrecision(18, 2);
        tiers.Property(x => x.Currency).HasMaxLength(3);
        tiers.Property<uint>("xmin").IsRowVersion();

        var parcels = model.Entity<Parcel>();
        parcels.ToTable("parcels", table =>
        {
            table.HasCheckConstraint("ck_parcels_origin", "(customer_id IS NOT NULL AND merchant_id IS NULL AND external_order_id IS NULL) OR (customer_id IS NULL AND merchant_id IS NOT NULL AND external_order_id IS NOT NULL AND btrim(external_order_id) <> '')");
            table.HasCheckConstraint("ck_parcels_weight", "weight_kg > 0 AND weight_kg <= 30");
            table.HasCheckConstraint("ck_parcels_size", $"size_category IN ({SizeValues})");
            table.HasCheckConstraint("ck_parcels_status", $"status IN ({StatusValues})");
            table.HasCheckConstraint("ck_parcels_version", "version >= 1");
            table.HasCheckConstraint("ck_parcels_details", "btrim(tracking_number) <> '' AND btrim(pickup_address) <> '' AND btrim(dropoff_address) <> '' AND btrim(dropoff_contact) <> ''");
        });
        parcels.HasKey(x => x.Id);
        parcels.HasAlternateKey(x => new { x.Id, x.ZoneId });
        parcels.Property(x => x.ExternalOrderId).HasMaxLength(128);
        parcels.Property(x => x.Status).HasConversion<string>();
        parcels.Property(x => x.Size).HasConversion<string>();
        parcels.Property(x => x.WeightKg).HasColumnType("numeric");
        parcels.Property(x => x.Version).IsConcurrencyToken();
        parcels.HasIndex(x => x.TrackingNumber).IsUnique();
        parcels.HasIndex(x => new { x.MerchantId, x.ExternalOrderId }).IsUnique()
            .HasFilter("merchant_id IS NOT NULL");
        parcels.HasIndex(x => new { x.CustomerId, x.CreatedAt, x.Id });
        parcels.HasOne<DeliveryZone>().WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);

        var quotes = model.Entity<Quote>();
        quotes.ToTable("quotes", table =>
        {
            table.HasCheckConstraint("ck_quotes_owner", "owner_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_quotes_input", "jsonb_typeof(delivery_input) = 'object' AND btrim(request_fingerprint) <> ''");
            table.HasCheckConstraint("ck_quotes_amount", "total >= 0 AND currency = 'LKR'");
            table.HasCheckConstraint("ck_quotes_expiry", "expires_at = created_at + INTERVAL '10 minutes'");
        });
        quotes.HasKey(x => x.Id);
        quotes.Property(x => x.DeliveryInput).HasColumnType("jsonb");
        quotes.Property(x => x.Total).HasPrecision(18, 2);
        quotes.Property(x => x.Currency).HasMaxLength(3);
        quotes.Property<uint>("xmin").IsRowVersion();
        quotes.HasIndex(x => new { x.OwnerId, x.ExpiresAt });
        quotes.HasIndex(x => x.ConsumedByParcelId).IsUnique();
        quotes.HasOne<Parcel>().WithOne().HasForeignKey<Quote>(x => x.ConsumedByParcelId).OnDelete(DeleteBehavior.Restrict);
        quotes.HasOne<PricingTier>().WithMany()
            .HasForeignKey(x => new { x.PricingTierId, x.PricingVersion, x.Currency })
            .HasPrincipalKey(x => new { x.Id, x.PricingVersion, x.Currency }).OnDelete(DeleteBehavior.Restrict);

        var snapshots = model.Entity<PriceSnapshot>();
        snapshots.ToTable("price_snapshots", table =>
        {
            table.HasCheckConstraint("ck_snapshots_amounts", "base_fee >= 0 AND weight_fee >= 0 AND total = base_fee + weight_fee");
            table.HasCheckConstraint("ck_snapshots_currency", "currency = 'LKR'");
        });
        snapshots.HasKey(x => x.Id);
        snapshots.Property(x => x.BaseFee).HasPrecision(18, 2);
        snapshots.Property(x => x.WeightFee).HasPrecision(18, 2);
        snapshots.Property(x => x.Total).HasPrecision(18, 2);
        snapshots.Property(x => x.Currency).HasMaxLength(3);
        snapshots.HasIndex(x => x.ParcelId).IsUnique();
        snapshots.HasOne<Parcel>().WithOne().HasForeignKey<PriceSnapshot>(x => new { x.ParcelId, x.ZoneId })
            .HasPrincipalKey<Parcel>(x => new { x.Id, x.ZoneId }).OnDelete(DeleteBehavior.Restrict);
        snapshots.HasOne<PricingTier>().WithMany()
            .HasForeignKey(x => new { x.PricingTierId, x.ZoneId, x.PricingVersion, x.Currency })
            .HasPrincipalKey(x => new { x.Id, x.ZoneId, x.PricingVersion, x.Currency }).OnDelete(DeleteBehavior.Restrict);

        var history = model.Entity<BookingHistory>();
        history.ToTable("booking_history", table =>
        {
            table.HasCheckConstraint("ck_history_status", $"to_status IN ({StatusValues}) AND (from_status IS NULL OR from_status IN ({StatusValues}))");
            table.HasCheckConstraint("ck_history_initial", "from_status IS NOT NULL OR to_status = 'Created'");
        });
        history.HasKey(x => x.Id);
        history.Property(x => x.FromStatus).HasConversion<string>();
        history.Property(x => x.ToStatus).HasConversion<string>();
        history.HasOne<Parcel>().WithMany().HasForeignKey(x => x.ParcelId).OnDelete(DeleteBehavior.Restrict);
        history.HasIndex(x => new { x.ParcelId, x.Timestamp });
        history.HasIndex(x => x.ParcelId).IsUnique().HasFilter("from_status IS NULL");

        var keys = model.Entity<IdempotencyRecord>();
        keys.ToTable("idempotency_keys", table =>
        {
            table.HasCheckConstraint("ck_idempotency_scope", "btrim(principal_id) <> '' AND btrim(operation) <> '' AND btrim(key) <> '' AND btrim(request_fingerprint) <> ''");
            table.HasCheckConstraint("ck_idempotency_expiry", "expires_at = created_at + INTERVAL '720 hours'");
            table.HasCheckConstraint("ck_idempotency_completion", "((parcel_id IS NULL AND stored_response IS NULL) OR (parcel_id IS NOT NULL AND stored_response IS NOT NULL AND jsonb_typeof(stored_response) = 'object' AND stored_response ?& ARRAY['statusCode','body','location'] AND stored_response->'statusCode' = '201'::jsonb AND jsonb_typeof(stored_response->'body') = 'string' AND jsonb_typeof(stored_response->'location') = 'string')) IS TRUE");
        });
        keys.HasKey(x => x.Id);
        keys.Property(x => x.Key).HasMaxLength(128);
        keys.Property(x => x.StoredResponse).HasColumnType("jsonb");
        keys.HasIndex(x => new { x.PrincipalId, x.Operation, x.Key }).IsUnique();
        keys.HasOne<Parcel>().WithMany().HasForeignKey(x => x.ParcelId).OnDelete(DeleteBehavior.Restrict);

        var outbox = model.Entity<OutboxMessage>();
        outbox.ToTable("outbox_messages", table =>
        {
            table.HasCheckConstraint("ck_outbox_payload", "(jsonb_typeof(payload) = 'object' AND event_type = 'StatusChanged' AND payload ?& ARRAY['eventId','eventType','schemaVersion','occurredAt','parcelId','parcelVersion','merchantId','status','previousStatus','correlationId'] AND payload - ARRAY['eventId','eventType','schemaVersion','occurredAt','parcelId','parcelVersion','merchantId','status','previousStatus','correlationId'] = '{}'::jsonb AND payload->>'eventId' = id::text AND payload->>'eventType' = event_type AND payload->'schemaVersion' = '1'::jsonb AND jsonb_typeof(payload->'parcelVersion') = 'number' AND (payload->>'parcelVersion')::bigint >= 1 AND jsonb_typeof(payload->'parcelId') = 'string' AND length(payload->>'parcelId') > 0 AND jsonb_typeof(payload->'correlationId') = 'string' AND length(payload->>'correlationId') > 0 AND jsonb_typeof(payload->'occurredAt') = 'string' AND (payload->>'occurredAt')::timestamptz = created_at AND (payload->>'status') IN ('Created','Assigned','PickedUp','InTransit','OutForDelivery','Delivered','Failed','Cancelled') AND (payload->'previousStatus' = 'null'::jsonb OR (payload->>'previousStatus') IN ('Created','Assigned','PickedUp','InTransit','OutForDelivery','Delivered','Failed','Cancelled')) AND (payload->'merchantId' = 'null'::jsonb OR jsonb_typeof(payload->'merchantId') = 'string')) IS TRUE");
        });
        outbox.HasKey(x => x.Id);
        outbox.Property(x => x.Payload).HasColumnType("jsonb");
        outbox.HasIndex(x => new { x.CreatedAt, x.Id }).HasFilter("published_at IS NULL");

        // One convention, no extra naming package. Explicit ER aliases follow it.
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(SnakeCase(property.Name));
            }
        }

        tiers.Property(x => x.Size).HasColumnName("size_category");
        parcels.Property(x => x.Size).HasColumnName("size_category");
        quotes.Property(x => x.ConsumedByParcelId).HasColumnName("consumed_by_parcel");
    }

    private static string SnakeCase(string value) => WordBoundary().Replace(value, "$1_$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();
}
