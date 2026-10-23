using System.Text.Json;

namespace SwiftDrop.Delivery.Api.Domain;

public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public static OutboxMessage ParcelCreated(Guid eventId, Parcel parcel, string correlationId)
    {
        DomainGuard.Id(eventId, nameof(eventId));
        DomainGuard.Text(correlationId, nameof(correlationId));
        return new OutboxMessage
        {
            Id = eventId,
            EventType = "StatusChanged",
            CreatedAt = parcel.CreatedAt,
            Payload = JsonSerializer.Serialize(new
            {
                eventId,
                eventType = "StatusChanged",
                schemaVersion = 1,
                occurredAt = parcel.CreatedAt,
                parcelId = parcel.Id.ToString(),
                parcelVersion = parcel.Version,
                merchantId = parcel.MerchantId?.ToString(),
                status = "Created",
                previousStatus = (string?)null,
                correlationId
            })
        };
    }

    public Guid Id { get; private set; }
    public string EventType { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
}
