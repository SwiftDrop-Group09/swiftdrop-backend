using MassTransit;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Contracts.Events.V1;
using SwiftDrop.Notification.Worker.Persistence;

namespace SwiftDrop.Notification.Worker.Messaging;

public sealed class StatusNotificationConsumer(
    NotificationDbContext db,
    TimeProvider clock,
    ILogger<StatusNotificationConsumer> logger) : IConsumer<StatusChangedV1>
{
    private const string ConsumerName = "status-notification";

    public async Task Consume(ConsumeContext<StatusChangedV1> context)
    {
        var message = context.Message;
        if (!message.IsValid(out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(context.CancellationToken);
        var eventKey = message.EventId.ToString("D");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({eventKey}, 0))",
            context.CancellationToken);
        var alreadyConsumed = await db.ConsumedStatusEvents.AnyAsync(
            x => x.Consumer == ConsumerName && x.EventId == message.EventId,
            context.CancellationToken);
        if (alreadyConsumed)
        {
            await transaction.CommitAsync(context.CancellationToken);
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

        db.NotificationWorkItems.Add(new NotificationWorkItem
        {
            EventId = message.EventId,
            Kind = message.MerchantId is null ? "customer-resolution" : "merchant-webhook-dispatch",
            OwnerId = message.MerchantId,
            ParcelId = message.ParcelId,
            ParcelVersion = message.ParcelVersion,
            Status = message.Status,
            CorrelationId = message.CorrelationId,
            State = "Pending",
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync(context.CancellationToken);
        await transaction.CommitAsync(context.CancellationToken);
        logger.LogInformation(
            "Recorded status event {EventId} for parcel {ParcelId} at version {ParcelVersion}",
            message.EventId,
            message.ParcelId,
            message.ParcelVersion);
    }
}
