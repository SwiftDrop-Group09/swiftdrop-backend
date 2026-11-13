namespace SwiftDrop.Merchant.Api.Domain.Orders;

/// <summary>
/// A point-in-time copy of a parcel's state as reported by Delivery.
/// <see cref="Version"/> is Delivery's monotonically increasing parcel version,
/// used to discard stale or out-of-order updates.
/// </summary>
public sealed record ParcelStatusSnapshot
{
    public ParcelStatusSnapshot(string status, long version, DateTimeOffset changedAt, DateTimeOffset? terminalAt = null)
    {
        Status = InboxJobGuard.ParcelStatus(status, nameof(status));
        Version = version >= 1 ? version : throw new ArgumentOutOfRangeException(nameof(version), "Parcel version starts at 1.");
        ChangedAt = changedAt.ToUniversalTime();
        TerminalAt = terminalAt?.ToUniversalTime();
    }

    public string Status { get; }
    public long Version { get; }
    public DateTimeOffset ChangedAt { get; }
    public DateTimeOffset? TerminalAt { get; }

    public bool IsTerminal => ParcelStatuses.IsTerminal(Status);
}
