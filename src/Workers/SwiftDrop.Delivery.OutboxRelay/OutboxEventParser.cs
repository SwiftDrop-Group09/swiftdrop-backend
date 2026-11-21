using System.Text.Json;
using SwiftDrop.Contracts.Events.V1;

namespace SwiftDrop.Delivery.OutboxRelay;

public static class OutboxEventParser
{
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    public static StatusChangedV1 Parse(Guid rowId, string eventType, string payload)
    {
        if (!string.Equals(eventType, StatusChangedV1.CanonicalEventType, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A Delivery outbox row has an unsupported event type.");
        }

        var message = JsonSerializer.Deserialize<StatusChangedV1>(payload, EventJson);
        if (message is null || message.EventId != rowId || !message.IsValid(out _))
        {
            throw new InvalidDataException("A Delivery outbox row does not match the StatusChanged v1 contract.");
        }

        return message;
    }
}
