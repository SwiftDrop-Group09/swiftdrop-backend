using System.Text.Json.Serialization;

namespace SwiftDrop.Contracts.Events.V1;

/// <summary>The privacy-safe, versioned parcel status side-effect event.</summary>
public sealed record StatusChangedV1(
    [property: JsonRequired] Guid EventId,
    [property: JsonRequired] string EventType,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] DateTimeOffset OccurredAt,
    [property: JsonRequired] string ParcelId,
    [property: JsonRequired] long ParcelVersion,
    [property: JsonRequired] string? MerchantId,
    [property: JsonRequired] string Status,
    [property: JsonRequired] string? PreviousStatus,
    [property: JsonRequired] string CorrelationId)
{
    public const string CanonicalEventType = "StatusChanged";
    public const int CurrentSchemaVersion = 1;

    private static readonly HashSet<string> ValidStatuses =
    [
        "Created", "Assigned", "PickedUp", "InTransit", "OutForDelivery",
        "Delivered", "Failed", "Cancelled"
    ];

    public bool IsValid(out string reason)
    {
        if (EventId == Guid.Empty || EventType != CanonicalEventType || SchemaVersion != CurrentSchemaVersion ||
            OccurredAt == default || OccurredAt.Offset != TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(ParcelId) || ParcelVersion < 1 ||
            string.IsNullOrWhiteSpace(CorrelationId) || !ValidStatuses.Contains(Status) ||
            (PreviousStatus is not null && !ValidStatuses.Contains(PreviousStatus)) ||
            (PreviousStatus is null && Status != "Created"))
        {
            reason = "StatusChanged v1 payload does not satisfy the version 1 contract.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
