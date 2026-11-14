namespace SwiftDrop.Merchant.Api.Domain.Orders;

/// <summary>
/// A merchant order held durably in the Merchant inbox until Delivery confirms
/// the parcel (FR-M04). The row is the merchant-visible order record (FR-M07)
/// and the source for the merchant dashboard (FR-M06).
/// </summary>
/// <remarks>
/// <para>State machine:</para>
/// <code>
/// Accepted ──► Created
///    │  ▲         │ ApplyStatus (version-ordered)
///    ▼  │         ▼
/// Retrying ──► Failed
/// </code>
/// <para>A job is never deleted or silently dropped. <c>(BusinessId, ExternalOrderId)</c>
/// is unique for the order's lifetime, independent of API key rotation.</para>
/// </remarks>
public sealed class InboxJob
{
    private InboxJob() { }

    // ── Identity and intake ────────────────────────────────────────────────
    public Guid Id { get; private set; }
    public Guid BusinessId { get; private set; }
    public string ExternalOrderId { get; private set; } = null!;
    public string IdempotencyKey { get; private set; } = null!;
    public string RequestFingerprint { get; private set; } = null!;

    /// <summary>Exact validated order JSON, replayed to Delivery on every attempt.</summary>
    public string OrderPayload { get; private set; } = null!;

    // ── Processing ─────────────────────────────────────────────────────────
    public InboxJobState State { get; private set; }

    /// <summary>Number of Delivery attempts that did not create the parcel.</summary>
    public int Attempts { get; private set; }

    /// <summary>When the processor should pick the job up next; null once the job is settled.</summary>
    public DateTimeOffset? NextRetryAt { get; private set; }

    /// <summary>Safe, merchant-visible snake_case code for the latest failure.</summary>
    public string? ErrorCode { get; private set; }

    // ── Delivery outcome (null until Created) ──────────────────────────────
    public Guid? ParcelId { get; private set; }
    public string? TrackingNumber { get; private set; }
    public string? AcceptedPriceVersion { get; private set; }
    public decimal? Total { get; private set; }
    public string? Currency { get; private set; }

    // ── Refreshed copy of Delivery's parcel state ──────────────────────────
    public string? ParcelStatus { get; private set; }
    public long? ParcelVersion { get; private set; }
    public DateTimeOffset? LastStatusChangedAt { get; private set; }
    public DateTimeOffset? TerminalAt { get; private set; }
    public DateTimeOffset? LastRefreshedAt { get; private set; }

    // ── Audit ──────────────────────────────────────────────────────────────
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Stores a newly received order. It is due for processing immediately.</summary>
    public static InboxJob Accept(
        Guid businessId,
        string externalOrderId,
        string idempotencyKey,
        string requestFingerprint,
        string orderPayload,
        DateTimeOffset now)
    {
        var timestamp = now.ToUniversalTime();
        return new InboxJob
        {
            Id = Guid.NewGuid(),
            BusinessId = DomainGuard.Id(businessId, nameof(businessId)),
            ExternalOrderId = InboxJobGuard.Identifier(externalOrderId, nameof(externalOrderId), InboxJobLimits.ExternalOrderIdMaxLength),
            IdempotencyKey = InboxJobGuard.Identifier(idempotencyKey, nameof(idempotencyKey), InboxJobLimits.IdempotencyKeyMaxLength),
            RequestFingerprint = InboxJobGuard.Fingerprint(requestFingerprint, nameof(requestFingerprint)),
            OrderPayload = InboxJobGuard.JsonObject(orderPayload, nameof(orderPayload)),
            State = InboxJobState.Accepted,
            Attempts = 0,
            NextRetryAt = timestamp,
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };
    }

    /// <summary>
    /// Leases the job to one processor by pushing <see cref="NextRetryAt"/> past
    /// the processing window. If the processor crashes mid-call, the job simply
    /// becomes due again when the lease expires; it is never lost.
    /// </summary>
    public void Claim(DateTimeOffset leaseUntil, DateTimeOffset now)
    {
        EnsureUnsettled(nameof(Claim));
        var timestamp = now.ToUniversalTime();
        var until = leaseUntil.ToUniversalTime();
        if (until <= timestamp)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseUntil), "A lease must end in the future.");
        }

        NextRetryAt = until;
        UpdatedAt = timestamp;
    }

    /// <summary>True when a resubmission carries exactly the same order content.</summary>
    public bool HasSameContentAs(string requestFingerprint) =>
        string.Equals(RequestFingerprint, requestFingerprint, StringComparison.Ordinal);

    /// <summary>Delivery created (or reconciliation found) the parcel for this order.</summary>
    public void MarkCreated(BookingConfirmation booking, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(booking);
        EnsureUnsettled(nameof(MarkCreated));

        var timestamp = now.ToUniversalTime();
        ParcelId = booking.ParcelId;
        TrackingNumber = booking.TrackingNumber;
        AcceptedPriceVersion = booking.PricingVersion;
        Total = booking.Total;
        Currency = booking.Currency;
        CopyParcelStatus(booking.ParcelStatus);

        State = InboxJobState.Created;
        NextRetryAt = null;
        ErrorCode = null;
        LastRefreshedAt = timestamp;
        UpdatedAt = timestamp;
    }

    /// <summary>
    /// Records a transient failure (timeout, Delivery unavailable) and schedules
    /// the next attempt using <see cref="InboxJobRetryPolicy"/>.
    /// </summary>
    public void ScheduleRetry(string errorCode, DateTimeOffset now)
    {
        var code = InboxJobGuard.ErrorCode(errorCode, nameof(errorCode));
        EnsureUnsettled(nameof(ScheduleRetry));

        var timestamp = now.ToUniversalTime();
        Attempts++;
        State = InboxJobState.Retrying;
        ErrorCode = code;
        NextRetryAt = timestamp + InboxJobRetryPolicy.DelayAfter(Attempts);
        UpdatedAt = timestamp;
    }

    /// <summary>Records a permanent rejection (invalid order, pricing changed, ...). The job stops here.</summary>
    public void MarkFailed(string errorCode, DateTimeOffset now)
    {
        var code = InboxJobGuard.ErrorCode(errorCode, nameof(errorCode));
        EnsureUnsettled(nameof(MarkFailed));

        Attempts++;
        State = InboxJobState.Failed;
        ErrorCode = code;
        NextRetryAt = null;
        UpdatedAt = now.ToUniversalTime();
    }

    /// <summary>
    /// Applies a newer parcel status from Delivery. Stale or duplicate versions
    /// are ignored, but the refresh time is always recorded so the dashboard
    /// can show how fresh its data is.
    /// </summary>
    /// <returns><c>true</c> when the snapshot was newer and has been applied.</returns>
    public bool ApplyStatus(ParcelStatusSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (State != InboxJobState.Created)
        {
            throw new InvalidOperationException($"Cannot apply a parcel status to an order in state '{State}'. Only Created orders have a parcel.");
        }

        var timestamp = now.ToUniversalTime();
        LastRefreshedAt = timestamp;
        UpdatedAt = timestamp;

        if (snapshot.Version <= ParcelVersion)
        {
            return false;
        }

        CopyParcelStatus(snapshot);
        return true;
    }

    private void CopyParcelStatus(ParcelStatusSnapshot snapshot)
    {
        ParcelStatus = snapshot.Status;
        ParcelVersion = snapshot.Version;
        LastStatusChangedAt = snapshot.ChangedAt;
        TerminalAt = snapshot.TerminalAt;
    }

    private void EnsureUnsettled(string operation)
    {
        if (State is not (InboxJobState.Accepted or InboxJobState.Retrying))
        {
            throw new InvalidOperationException($"Cannot {operation} an order in state '{State}'. Only Accepted or Retrying orders are still being processed.");
        }
    }
}
