using Microsoft.EntityFrameworkCore;

namespace SwiftDrop.Notification.Worker.Persistence;

public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<ConsumedStatusEvent> ConsumedStatusEvents => Set<ConsumedStatusEvent>();
    public DbSet<NotificationWorkItem> NotificationWorkItems => Set<NotificationWorkItem>();

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

        modelBuilder.Entity<NotificationWorkItem>(entity =>
        {
            entity.ToTable("notification_work_items");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.EventId, x.Kind }).IsUnique()
                .HasDatabaseName("ux_notification_work_items_event_id_kind");
            entity.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            entity.Property(x => x.State).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ParcelId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(160).IsRequired();
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

public sealed class NotificationWorkItem
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? OwnerId { get; set; }
    public string ParcelId { get; set; } = string.Empty;
    public long ParcelVersion { get; set; }
    public string Status { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string State { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
