using Microsoft.EntityFrameworkCore;

namespace SwiftDrop.Notification.Worker.Persistence;

public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<ConsumedStatusEvent> ConsumedStatusEvents => Set<ConsumedStatusEvent>();
    public DbSet<NotificationJob> NotificationJobs => Set<NotificationJob>();
    public DbSet<NotificationProviderAttempt> NotificationProviderAttempts => Set<NotificationProviderAttempt>();
    public DbSet<WebhookDispatchJob> WebhookDispatchJobs => Set<WebhookDispatchJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConsumedStatusEvent>(entity =>
        {
            entity.ToTable("consumed_status_events");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Consumer, x.EventId }).IsUnique()
                .HasDatabaseName("ux_consumed_status_events_consumer_event_id");
            entity.Property(x => x.Consumer).HasMaxLength(80).IsRequired();
            entity.Property(x => x.EventId).IsRequired();
            entity.Property(x => x.EventType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.ParcelId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.OccurredAt).IsRequired();
            entity.Property(x => x.ConsumedAt).IsRequired();
        });

        modelBuilder.Entity<NotificationJob>(entity =>
        {
            entity.ToTable("notification_jobs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.EventId, x.RecipientId, x.Channel }).IsUnique()
                .HasFilter("\"RecipientId\" IS NOT NULL")
                .HasDatabaseName("ux_notification_jobs_event_recipient_channel");
            entity.HasIndex(x => new { x.EventId, x.Channel }).IsUnique()
                .HasFilter("\"RecipientId\" IS NULL")
                .HasDatabaseName("ux_notification_jobs_unresolved_event_channel");
            entity.HasIndex(x => new { x.State, x.CreatedAt })
                .HasDatabaseName("ix_notification_jobs_state_created");
            entity.Property(x => x.Channel).HasMaxLength(24).IsRequired();
            entity.Property(x => x.State).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ParcelId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.OccurredAt).IsRequired();
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();
        });

        modelBuilder.Entity<NotificationProviderAttempt>(entity =>
        {
            entity.ToTable("notification_provider_attempts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.NotificationJobId, x.AttemptNumber }).IsUnique()
                .HasDatabaseName("ux_notification_provider_attempts_job_attempt");
            entity.Property(x => x.Outcome).HasMaxLength(24).IsRequired();
            entity.Property(x => x.FailureCode).HasMaxLength(80);
            entity.Property(x => x.StartedAt).IsRequired();
            entity.HasOne<NotificationJob>()
                .WithMany()
                .HasForeignKey(x => x.NotificationJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebhookDispatchJob>(entity =>
        {
            entity.ToTable("webhook_dispatch_jobs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.EventId, x.MerchantId }).IsUnique()
                .HasDatabaseName("ux_webhook_dispatch_jobs_event_merchant");
            entity.HasIndex(x => new { x.State, x.CreatedAt })
                .HasDatabaseName("ix_webhook_dispatch_jobs_state_created");
            entity.Property(x => x.MerchantId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ParcelId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.State).HasMaxLength(32).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.OccurredAt).IsRequired();
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();
        });
    }
}

public sealed class ConsumedStatusEvent
{
    public long Id { get; set; }
    public string Consumer { get; set; } = "status-notification";
    public Guid EventId { get; set; }
    public string EventType { get; set; } = "StatusChanged";
    public string ParcelId { get; set; } = string.Empty;
    public long ParcelVersion { get; set; }
    public string Status { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ConsumedAt { get; set; }
}

public sealed class NotificationJob
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public Guid? RecipientId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string ParcelId { get; set; } = string.Empty;
    public long ParcelVersion { get; set; }
    public string Status { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string State { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class NotificationProviderAttempt
{
    public long Id { get; set; }
    public long NotificationJobId { get; set; }
    public int AttemptNumber { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? FailureCode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class WebhookDispatchJob
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public string MerchantId { get; set; } = string.Empty;
    public string ParcelId { get; set; } = string.Empty;
    public long ParcelVersion { get; set; }
    public string Status { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string State { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
