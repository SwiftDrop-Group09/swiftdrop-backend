using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence;

internal static class CourierEntityConfigurations
{
    public static void Configure(ModelBuilder model)
    {
        var couriers = model.Entity<Courier>();
        couriers.ToTable("couriers", table =>
        {
            table.HasCheckConstraint("ck_couriers_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_couriers_capacity", "capacity >= 1");
            table.HasCheckConstraint("ck_couriers_version", "version >= 1");
        });
        couriers.HasKey(x => x.Id);
        // Identity owns this ID; EF must not generate a different operational identity.
        couriers.Property(x => x.Id).ValueGeneratedNever();
        couriers.Property(x => x.Version).IsConcurrencyToken();

        var zones = model.Entity<CourierZone>();
        zones.ToTable("courier_zones");
        zones.HasKey(x => new { x.CourierId, x.ZoneId });
        zones.HasOne<Courier>().WithMany().HasForeignKey(x => x.CourierId).OnDelete(DeleteBehavior.Restrict);
        zones.HasOne<DeliveryZone>().WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);

        var assignments = model.Entity<CourierAssignment>();
        assignments.ToTable("assignments", table =>
        {
            table.HasCheckConstraint("ck_assignments_status", "status IN ('Active','Reassigned','Delivered','Failed','Cancelled')");
            table.HasCheckConstraint("ck_assignments_timestamps", "(picked_up_at IS NULL OR picked_up_at >= assigned_at) AND (delivered_at IS NULL OR (picked_up_at IS NOT NULL AND delivered_at >= picked_up_at))");
            table.HasCheckConstraint("ck_assignments_delivery", "(status = 'Delivered' AND delivered_at IS NOT NULL AND picked_up_at IS NOT NULL) OR (status <> 'Delivered' AND delivered_at IS NULL)");
            table.HasCheckConstraint("ck_assignments_release", "status NOT IN ('Reassigned','Cancelled') OR picked_up_at IS NULL");
        });
        assignments.HasKey(x => x.Id);
        assignments.Property(x => x.Status).HasConversion<string>();
        assignments.Property<uint>("xmin").IsRowVersion();
        assignments.HasOne<Parcel>().WithMany().HasForeignKey(x => x.ParcelId).OnDelete(DeleteBehavior.Restrict);
        assignments.HasOne<Courier>().WithMany().HasForeignKey(x => x.CourierId).OnDelete(DeleteBehavior.Restrict);
        assignments.HasIndex(x => x.ParcelId).IsUnique().HasFilter("status = 'Active'")
            .HasDatabaseName("ux_assignments_current_parcel");
        assignments.HasIndex(x => new { x.CourierId, x.ServiceDate, x.AssignedAt, x.Id })
            .HasFilter("status = 'Active'");
        assignments.HasIndex(x => new { x.CourierId, x.Status, x.AssignedAt, x.Id });

        var proof = model.Entity<ProofMetadata>();
        proof.ToTable("proof_metadata", table =>
        {
            table.HasCheckConstraint("ck_proof_type", "type IN ('Photo','Signature')");
            table.HasCheckConstraint("ck_proof_storage_ref", "btrim(storage_ref) <> '' AND storage_ref !~ '^[A-Za-z][A-Za-z0-9+.-]*:' AND storage_ref !~ '^//'");
            table.HasCheckConstraint("ck_proof_recipient", "btrim(recipient_name) <> ''");
            table.HasCheckConstraint("ck_proof_actor", "uploaded_by <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        proof.HasKey(x => x.Id);
        proof.Property(x => x.Type).HasConversion<string>();
        proof.Property(x => x.RecipientName).HasMaxLength(120);
        proof.HasOne<Parcel>().WithMany().HasForeignKey(x => x.ParcelId).OnDelete(DeleteBehavior.Restrict);
        proof.HasIndex(x => x.StorageRef).IsUnique();
        proof.HasIndex(x => new { x.ParcelId, x.UploadedAt, x.Id });
    }
}
