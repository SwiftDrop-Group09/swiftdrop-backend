namespace SwiftDrop.Merchant.Api.Domain.Webhooks;

public sealed class WebhookDeliveryJob
{
    private WebhookDeliveryJob() { }

    public WebhookDeliveryJob(
        Guid id,
        Guid eventId,
        Guid subscriptionId,
        int replayGeneration,
        byte[] frozenEventBytes,
        int secretVersion,
        WebhookStatus status,
        int attemptCount,
        DateTimeOffset dueAt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? leasedUntil = null,
        Guid? leaseToken = null,
        string? safeErrorCode = null)
    {
        Id = WebhookGuard.Id(id, nameof(id));
        EventId = WebhookGuard.Id(eventId, nameof(eventId));
        SubscriptionId = WebhookGuard.Id(subscriptionId, nameof(subscriptionId));
        ReplayGeneration = replayGeneration >= 0 ? replayGeneration : throw new ArgumentOutOfRangeException(nameof(replayGeneration));
        FrozenEventBytes = WebhookGuard.NonEmptyBytes(frozenEventBytes, nameof(frozenEventBytes));
        SecretVersion = secretVersion > 0 ? secretVersion : throw new ArgumentOutOfRangeException(nameof(secretVersion));
        Status = status;
        AttemptCount = attemptCount >= 0 ? attemptCount : throw new ArgumentOutOfRangeException(nameof(attemptCount));
        DueAt = dueAt.ToUniversalTime();
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
        LeasedUntil = leasedUntil?.ToUniversalTime();
        LeaseToken = leaseToken;
        SafeErrorCode = safeErrorCode;
    }

    public static WebhookDeliveryJob CreateAutomatic(
        Guid eventId,
        Guid subscriptionId,
        byte[] frozenEventBytes,
        int secretVersion,
        bool requiresResolution = false,
        DateTimeOffset? now = null)
    {
        var timestamp = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var initialStatus = requiresResolution ? WebhookStatus.PendingResolution : WebhookStatus.Pending;

        return new WebhookDeliveryJob(
            id: Guid.NewGuid(),
            eventId: eventId,
            subscriptionId: subscriptionId,
            replayGeneration: 0,
            frozenEventBytes: frozenEventBytes,
            secretVersion: secretVersion,
            status: initialStatus,
            attemptCount: 0,
            dueAt: timestamp,
            createdAt: timestamp,
            updatedAt: timestamp);
    }

    public void ResolveTracking(byte[] updatedBytes, DateTimeOffset now)
    {
        if (Status != WebhookStatus.PendingResolution)
        {
            return;
        }

        FrozenEventBytes = WebhookGuard.NonEmptyBytes(updatedBytes, nameof(updatedBytes));
        Status = WebhookStatus.Pending;
        UpdatedAt = now.ToUniversalTime();
    }

    public void Claim(DateTimeOffset leaseUntil, Guid leaseToken, DateTimeOffset now)
    {
        if (Status != WebhookStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot claim webhook job in status '{Status}'. Only Pending jobs can be claimed.");
        }

        Status = WebhookStatus.Processing;
        LeasedUntil = leaseUntil.ToUniversalTime();
        LeaseToken = leaseToken;
        UpdatedAt = now.ToUniversalTime();
    }

    public void RecordAttempt(bool delivered, DateTimeOffset? nextRetryAt, string? safeErrorCode, DateTimeOffset now)
    {
        AttemptCount++;
        LeasedUntil = null;
        LeaseToken = null;
        UpdatedAt = now.ToUniversalTime();
        SafeErrorCode = safeErrorCode;

        if (delivered)
        {
            Status = WebhookStatus.Delivered;
            SafeErrorCode = null;
        }
        else if (nextRetryAt.HasValue && AttemptCount < 6)
        {
            Status = WebhookStatus.Pending;
            DueAt = nextRetryAt.Value.ToUniversalTime();
        }
        else
        {
            Status = WebhookStatus.Failed;
        }
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status == WebhookStatus.Delivered || Status == WebhookStatus.Cancelled)
        {
            return;
        }

        Status = WebhookStatus.Cancelled;
        LeasedUntil = null;
        LeaseToken = null;
        UpdatedAt = now.ToUniversalTime();
    }

    public (WebhookDeliveryJob ReplayJob, WebhookReplayAudit Audit) CreateReplay(
        Guid actorId,
        Guid businessId,
        string reason,
        DateTimeOffset now)
    {
        if (Status != WebhookStatus.Failed)
        {
            throw new InvalidOperationException($"Only failed webhook jobs can be manually replayed. Current status: '{Status}'.");
        }

        var timestamp = now.ToUniversalTime();
        var replayJob = new WebhookDeliveryJob(
            id: Guid.NewGuid(),
            eventId: EventId,
            subscriptionId: SubscriptionId,
            replayGeneration: ReplayGeneration + 1,
            frozenEventBytes: FrozenEventBytes,
            secretVersion: SecretVersion,
            status: WebhookStatus.Pending,
            attemptCount: 0,
            dueAt: timestamp,
            createdAt: timestamp,
            updatedAt: timestamp);

        var audit = new WebhookReplayAudit(
            id: Guid.NewGuid(),
            actorId: actorId,
            businessId: businessId,
            subscriptionId: SubscriptionId,
            originalJobId: Id,
            replayJobId: replayJob.Id,
            reason: reason,
            createdAt: timestamp);

        return (replayJob, audit);
    }

    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public Guid SubscriptionId { get; private set; }
    public int ReplayGeneration { get; private set; }
    public byte[] FrozenEventBytes { get; private set; } = [];
    public int SecretVersion { get; private set; }
    public WebhookStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset DueAt { get; private set; }
    public DateTimeOffset? LeasedUntil { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public string? SafeErrorCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
