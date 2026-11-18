namespace SwiftDrop.Merchant.Api.Domain.Orders;

/// <summary>
/// A point-in-time copy of a parcel's state as reported by Delivery.
/// <see cref="Version"/> is Delivery's monotonically increasing parcel version,
/// used to discard stale or out-of-order updates.
/// </summary>
public sealed record ParcelStatusSnapshot
{
    public ParcelStatusSnapshot(string status, long version, DateTimeOffset changedAt, DateTimeOffset? terminalAt = null, string? failureReason = null)
    {
        Status = InboxJobGuard.ParcelStatus(status, nameof(status));
        Version = version >= 1 ? version : throw new ArgumentOutOfRangeException(nameof(version), "Parcel version starts at 1.");
        ChangedAt = changedAt.ToUniversalTime();
        TerminalAt = terminalAt?.ToUniversalTime();
        if (failureReason is not null && (status != "Failed" || failureReason is not
            ("RecipientUnavailable" or "AddressUnreachable" or "Damaged" or "Other")))
            throw new ArgumentException("Only a canonical failure category is accepted.", nameof(failureReason));
        FailureReason = failureReason;
    }

    public string Status { get; }
    public long Version { get; }
    public DateTimeOffset ChangedAt { get; }
    public DateTimeOffset? TerminalAt { get; }

    public string? FailureReason { get; }

    public bool IsTerminal => ParcelStatuses.IsTerminal(Status);
}
