using MassTransit;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Contracts.Events.V1;
using SwiftDrop.Notification.Worker.Persistence;

namespace SwiftDrop.Notification.Worker.Messaging;

public sealed class StatusNotificationConsumer(
    StatusNotificationProcessor processor,
    ILogger<StatusNotificationConsumer> logger) : IConsumer<StatusChangedV1>
{
    public async Task Consume(ConsumeContext<StatusChangedV1> context)
    {
        await processor.ProcessAsync(context.Message, context.CancellationToken);
        logger.LogInformation("Processed StatusChanged event {EventId}", context.Message.EventId);
    }
}

public sealed class StatusNotificationProcessor(
    NotificationDbContext db,
    TimeProvider clock,
    ILogger<StatusNotificationProcessor> logger)
{
    private const string ConsumerName = "status-notification";

    public async Task ProcessAsync(StatusChangedV1 message, CancellationToken cancellationToken)
    {
        if (!message.IsValid(out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize a parcel's versions across Worker replicas. The unique inbox index
        // remains the final guard against duplicate delivery of the same event.
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            var parcelKey = message.ParcelId;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({parcelKey}, 0))",
                cancellationToken);
        }

        var alreadyConsumed = await db.ConsumedStatusEvents.AnyAsync(
            x => x.Consumer == ConsumerName && x.EventId == message.EventId,
            cancellationToken);
        if (alreadyConsumed)
        {
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Skipped duplicate status event {EventId}", message.EventId);
            return;
        }

        var now = clock.GetUtcNow();
        db.ConsumedStatusEvents.Add(new ConsumedStatusEvent
        {
            Consumer = ConsumerName,
            EventId = message.EventId,
            EventType = message.EventType,
            ParcelId = message.ParcelId,
            ParcelVersion = message.ParcelVersion,
            Status = message.Status,
            CorrelationId = message.CorrelationId,
            OccurredAt = message.OccurredAt,
            ConsumedAt = now
        });

        var latestParcelVersion = await db.ConsumedStatusEvents
            .Where(x => x.Consumer == ConsumerName && x.ParcelId == message.ParcelId && x.EventId != message.EventId)
            .Select(x => (long?)x.ParcelVersion)
            .MaxAsync(cancellationToken);
        if (latestParcelVersion is not null && message.ParcelVersion <= latestParcelVersion.Value)
        {
            // Keep the inbox record for audit and broker replay safety, but do not
            // create user-visible side effects from an older (or repeated) version.
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Recorded stale status event {EventId} at parcel version {ParcelVersion}; latest consumed version is {LatestParcelVersion}",
                message.EventId,
                message.ParcelVersion,
                latestParcelVersion.Value);
            return;
        }

        if (message.MerchantId is null)
        {
            db.NotificationJobs.AddRange(
                CreateNotificationJob(message, "InApp", now),
                CreateNotificationJob(message, "MockSms", now));
        }
        else
        {
            db.WebhookDispatchJobs.Add(new WebhookDispatchJob
            {
                EventId = message.EventId,
                MerchantId = message.MerchantId,
                ParcelId = message.ParcelId,
                ParcelVersion = message.ParcelVersion,
                Status = message.Status,
                CorrelationId = message.CorrelationId,
                OccurredAt = message.OccurredAt,
                State = "Pending",
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        // EF Core commits this inbox row and its derived jobs in one transaction.
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Recorded status event {EventId} at parcel version {ParcelVersion}",
            message.EventId,
            message.ParcelVersion);
    }

    private static NotificationJob CreateNotificationJob(
        StatusChangedV1 message,
        string channel,
        DateTimeOffset now) => new()
    {
        EventId = message.EventId,
        Channel = channel,
        ParcelId = message.ParcelId,
        ParcelVersion = message.ParcelVersion,
        Status = message.Status,
        CorrelationId = message.CorrelationId,
        OccurredAt = message.OccurredAt,
        State = "PendingRecipientResolution",
        CreatedAt = now,
        UpdatedAt = now
    };
}
