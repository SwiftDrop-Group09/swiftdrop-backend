using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Domain.Webhooks;

namespace SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Persistence;

public static class WebhookEntityConfigurations
{
    private const string JobStatusValues = "'Pending','PendingResolution','Processing','Delivered','Failed','Cancelled'";

    public static void Configure(ModelBuilder model)
    {
        // 1. WebhookSubscription -> webhook_subscriptions
        var subscriptions = model.Entity<WebhookSubscription>();
        subscriptions.ToTable("webhook_subscriptions", table =>
        {
            table.HasCheckConstraint("ck_webhook_subscriptions_endpoint", "btrim(endpoint_url) <> ''");
            table.HasCheckConstraint("ck_webhook_subscriptions_key_version", "key_version > 0");
            table.HasCheckConstraint("ck_webhook_subscriptions_secret_version", "secret_version > 0");
        });

        subscriptions.HasKey(x => x.Id);
        subscriptions.Property(x => x.Id).ValueGeneratedNever();
        subscriptions.Property(x => x.BusinessId).IsRequired();
        subscriptions.Property(x => x.EndpointUrl).HasMaxLength(2048).IsRequired();

        var setConverter = new ValueConverter<IReadOnlySet<string>, string>(
            set => string.Join(',', set),
            str => str.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .ToHashSet(StringComparer.OrdinalIgnoreCase));

        var setComparer = new ValueComparer<IReadOnlySet<string>>(
            (c1, c2) => c1 != null && c2 != null && c1.SetEquals(c2),
            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => c.ToHashSet(StringComparer.OrdinalIgnoreCase));

        subscriptions.Property(x => x.SubscribedEvents)
            .HasConversion(setConverter, setComparer)
            .HasMaxLength(500)
            .IsRequired();

        subscriptions.Property(x => x.SecretCiphertext).IsRequired();
        subscriptions.Property(x => x.SecretNonce).HasMaxLength(12).IsRequired();
        subscriptions.Property(x => x.SecretTag).HasMaxLength(16).IsRequired();
        subscriptions.Property(x => x.KeyVersion).IsRequired();
        subscriptions.Property(x => x.SecretVersion).IsRequired();
        subscriptions.Property(x => x.IsActive).IsRequired();
        subscriptions.Property(x => x.CreatedAt).IsRequired();
        subscriptions.Property(x => x.UpdatedAt).IsRequired();

        subscriptions.HasIndex(x => x.BusinessId);
        subscriptions.HasIndex(x => new { x.BusinessId, x.IsActive });

        subscriptions.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        // 2. WebhookDeliveryJob -> webhook_dispatch_jobs
        var jobs = model.Entity<WebhookDeliveryJob>();
        jobs.ToTable("webhook_dispatch_jobs", table =>
        {
            table.HasCheckConstraint("ck_webhook_dispatch_jobs_status", $"status IN ({JobStatusValues})");
            table.HasCheckConstraint("ck_webhook_dispatch_jobs_replay_gen", "replay_generation >= 0");
            table.HasCheckConstraint("ck_webhook_dispatch_jobs_attempts", "attempt_count >= 0");
        });

        jobs.HasKey(x => x.Id);
        jobs.Property(x => x.Id).ValueGeneratedNever();
        jobs.Property(x => x.EventId).IsRequired();
        jobs.Property(x => x.SubscriptionId).IsRequired();
        jobs.Property(x => x.ReplayGeneration).IsRequired();
        jobs.Property(x => x.FrozenEventBytes).IsRequired();
        jobs.Property(x => x.SecretVersion).IsRequired();
        jobs.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        jobs.Property(x => x.AttemptCount).IsRequired();
        jobs.Property(x => x.DueAt).IsRequired();
        jobs.Property(x => x.LeasedUntil);
        jobs.Property(x => x.LeaseToken);
        jobs.Property(x => x.SafeErrorCode).HasMaxLength(64);
        jobs.Property(x => x.CreatedAt).IsRequired();
        jobs.Property(x => x.UpdatedAt).IsRequired();

        // Idempotency: Unique on (event_id, subscription_id, replay_generation)
        jobs.HasIndex(x => new { x.EventId, x.SubscriptionId, x.ReplayGeneration }).IsUnique();
        jobs.HasIndex(x => new { x.Status, x.DueAt });
        jobs.HasIndex(x => x.SubscriptionId);

        jobs.HasOne<WebhookSubscription>()
            .WithMany()
            .HasForeignKey(x => x.SubscriptionId)
            .OnDelete(DeleteBehavior.Cascade);

        // 3. WebhookAttempt -> webhook_attempts
        var attempts = model.Entity<WebhookAttempt>();
        attempts.ToTable("webhook_attempts", table =>
        {
            table.HasCheckConstraint("ck_webhook_attempts_number", "attempt_number > 0");
            table.HasCheckConstraint("ck_webhook_attempts_duration", "duration_ms >= 0");
        });

        attempts.HasKey(x => x.Id);
        attempts.Property(x => x.Id).ValueGeneratedNever();
        attempts.Property(x => x.JobId).IsRequired();
        attempts.Property(x => x.AttemptNumber).IsRequired();
        attempts.Property(x => x.RequestedAt).IsRequired();
        attempts.Property(x => x.ResponseStatusCode);
        attempts.Property(x => x.DurationMs).IsRequired();
        attempts.Property(x => x.NextRetryAt);
        attempts.Property(x => x.FailureReason).HasMaxLength(200);
        attempts.Property(x => x.CreatedAt).IsRequired();

        attempts.HasIndex(x => x.JobId);
        attempts.HasIndex(x => new { x.JobId, x.AttemptNumber });

        attempts.HasOne<WebhookDeliveryJob>()
            .WithMany()
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        // 4. WebhookReplayAudit -> webhook_replay_audits
        var audits = model.Entity<WebhookReplayAudit>();
        audits.ToTable("webhook_replay_audits", table =>
        {
            table.HasCheckConstraint("ck_webhook_replay_audits_reason", "btrim(reason) <> ''");
        });

        audits.HasKey(x => x.Id);
        audits.Property(x => x.Id).ValueGeneratedNever();
        audits.Property(x => x.ActorId).IsRequired();
        audits.Property(x => x.BusinessId).IsRequired();
        audits.Property(x => x.SubscriptionId).IsRequired();
        audits.Property(x => x.OriginalJobId).IsRequired();
        audits.Property(x => x.ReplayJobId).IsRequired();
        audits.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        audits.Property(x => x.CreatedAt).IsRequired();

        audits.HasIndex(x => x.BusinessId);
        audits.HasIndex(x => x.SubscriptionId);
        audits.HasIndex(x => x.OriginalJobId);

        audits.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        audits.HasOne<WebhookSubscription>()
            .WithMany()
            .HasForeignKey(x => x.SubscriptionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
