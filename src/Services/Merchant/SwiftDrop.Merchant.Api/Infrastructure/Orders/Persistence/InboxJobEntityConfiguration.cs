using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Domain.Orders;

namespace SwiftDrop.Merchant.Api.Infrastructure.Orders.Persistence;

/// <summary>
/// Maps <see cref="InboxJob"/> to <c>inbox_jobs</c>. Column names become
/// snake_case through the shared convention in <c>MerchantEntityConfigurations</c>,
/// so the SQL in the check constraints below uses snake_case names.
/// </summary>
internal sealed class InboxJobEntityConfiguration : IEntityTypeConfiguration<InboxJob>
{
    public const string TableName = "inbox_jobs";

    private static readonly string StateValues =
        string.Join(',', Enum.GetNames<InboxJobState>().Select(name => $"'{name}'"));

    public void Configure(EntityTypeBuilder<InboxJob> job)
    {
        job.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_inbox_jobs_state", $"state IN ({StateValues})");
            table.HasCheckConstraint("ck_inbox_jobs_external_order_id", "btrim(external_order_id) <> ''");
            table.HasCheckConstraint("ck_inbox_jobs_idempotency_key", "btrim(idempotency_key) <> ''");
            table.HasCheckConstraint("ck_inbox_jobs_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("ck_inbox_jobs_attempts", "attempts >= 0");
            table.HasCheckConstraint("ck_inbox_jobs_total", "total IS NULL OR total >= 0");
            table.HasCheckConstraint("ck_inbox_jobs_parcel_version", "parcel_version IS NULL OR parcel_version >= 1");

            // State invariants: unsettled jobs must be schedulable, created jobs must carry their parcel.
            table.HasCheckConstraint(
                "ck_inbox_jobs_unsettled_is_scheduled",
                $"state NOT IN ('{nameof(InboxJobState.Accepted)}','{nameof(InboxJobState.Retrying)}') OR next_retry_at IS NOT NULL");
            table.HasCheckConstraint(
                "ck_inbox_jobs_created_has_parcel",
                $"state <> '{nameof(InboxJobState.Created)}' OR " +
                "(parcel_id IS NOT NULL AND tracking_number IS NOT NULL AND parcel_status IS NOT NULL AND parcel_version IS NOT NULL)");
        });

        job.HasKey(x => x.Id);
        job.Property(x => x.Id).ValueGeneratedNever();

        // Intake
        job.Property(x => x.BusinessId).IsRequired();
        job.Property(x => x.ExternalOrderId).HasMaxLength(InboxJobLimits.ExternalOrderIdMaxLength).IsRequired();
        job.Property(x => x.IdempotencyKey).HasMaxLength(InboxJobLimits.IdempotencyKeyMaxLength).IsRequired();
        job.Property(x => x.RequestFingerprint).HasMaxLength(InboxJobLimits.FingerprintLength).IsFixedLength().IsRequired();
        job.Property(x => x.OrderPayload).HasColumnType("jsonb").IsRequired();

        // Processing
        job.Property(x => x.State).HasConversion<string>().HasMaxLength(InboxJobLimits.StateMaxLength).IsRequired();
        job.Property(x => x.Attempts).IsRequired();
        job.Property(x => x.ErrorCode).HasMaxLength(InboxJobLimits.ErrorCodeMaxLength);

        // Delivery outcome and refreshed parcel state
        job.Property(x => x.TrackingNumber).HasMaxLength(InboxJobLimits.TrackingNumberMaxLength);
        job.Property(x => x.AcceptedPriceVersion).HasMaxLength(InboxJobLimits.PricingVersionMaxLength);
        job.Property(x => x.Total).HasPrecision(InboxJobLimits.TotalPrecision, InboxJobLimits.TotalScale);
        job.Property(x => x.Currency).HasMaxLength(InboxJobLimits.CurrencyLength).IsFixedLength();
        job.Property(x => x.ParcelStatus).HasMaxLength(InboxJobLimits.ParcelStatusMaxLength);

        job.Property(x => x.CreatedAt).IsRequired();
        job.Property(x => x.UpdatedAt).IsRequired();

        // One order per merchant external id, for the order's whole lifetime.
        job.HasIndex(x => new { x.BusinessId, x.ExternalOrderId }).IsUnique();
        // Processor: claim due Accepted/Retrying jobs.
        job.HasIndex(x => new { x.State, x.NextRetryAt });
        // Merchant order list: stable newest-first paging within one business.
        job.HasIndex(x => new { x.BusinessId, x.CreatedAt, x.Id });
        // Intake: detect an Idempotency-Key reused for a different order.
        job.HasIndex(x => new { x.BusinessId, x.IdempotencyKey });

        // Orders are lifetime records: a business with orders cannot be hard-deleted.
        job.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
