using System.Text.Json;

namespace SwiftDrop.Delivery.Api.Domain;

public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public static OutboxMessage ParcelCreated(Guid eventId, Parcel parcel, string correlationId)
        => Create(eventId, parcel, "Created", null, parcel.CreatedAt, correlationId);

    public static OutboxMessage CourierChanged(Guid eventId, Parcel parcel, ParcelStatus previousStatus, string correlationId)
    {
        if (!CourierTransitionPolicy.IsAllowed(previousStatus, parcel.Status) || parcel.Version < 2)
            throw new InvalidOperationException("The event must describe an accepted Courier transition.");
        return Create(eventId, parcel, parcel.Status.ToString(), previousStatus.ToString(), parcel.UpdatedAt, correlationId);
    }

    private static OutboxMessage Create(Guid eventId, Parcel parcel, string status, string? previousStatus,
        DateTimeOffset occurredAt, string correlationId)
    {
        DomainGuard.Id(eventId, nameof(eventId));
        DomainGuard.Text(correlationId, nameof(correlationId));
        return new OutboxMessage
        {
            Id = eventId,
            EventType = "StatusChanged",
            CreatedAt = occurredAt,
            Payload = JsonSerializer.Serialize(new
            {
                eventId,
                eventType = "StatusChanged",
                schemaVersion = 1,
                occurredAt,
                parcelId = parcel.Id.ToString(),
                parcelVersion = parcel.Version,
                merchantId = parcel.MerchantId?.ToString(),
                status,
                previousStatus,
                correlationId
            })
        };
    }

    public static OutboxMessage ProofCompleted(Guid eventId, Parcel parcel, string correlationId)
    {
        if (parcel.Status != ParcelStatus.Delivered || parcel.Version < 2)
            throw new InvalidOperationException("Completion event requires Delivered.");
        return Create(eventId, parcel, "Delivered", "OutForDelivery", parcel.UpdatedAt, correlationId);
    }

    public Guid Id { get; private set; }
    public string EventType { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
}
