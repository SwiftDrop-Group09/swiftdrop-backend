namespace SwiftDrop.Merchant.Api.Domain.Webhooks;

public sealed class WebhookAttempt
{
    private WebhookAttempt() { }

    public WebhookAttempt(
        Guid id,
        Guid jobId,
        int attemptNumber,
        DateTimeOffset requestedAt,
        int? responseStatusCode,
        long durationMs,
        DateTimeOffset? nextRetryAt,
        string? failureReason,
        DateTimeOffset createdAt)
    {
        Id = WebhookGuard.Id(id, nameof(id));
        JobId = WebhookGuard.Id(jobId, nameof(jobId));
        AttemptNumber = attemptNumber > 0 ? attemptNumber : throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        RequestedAt = requestedAt.ToUniversalTime();
        ResponseStatusCode = responseStatusCode;
        DurationMs = durationMs >= 0 ? durationMs : throw new ArgumentOutOfRangeException(nameof(durationMs));
        NextRetryAt = nextRetryAt?.ToUniversalTime();
        FailureReason = failureReason?.Length > 200 ? failureReason[..200] : failureReason;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public static WebhookAttempt Record(
        Guid jobId,
        int attemptNumber,
        DateTimeOffset requestedAt,
        int? responseStatusCode,
        long durationMs,
        DateTimeOffset? nextRetryAt,
        string? failureReason,
        DateTimeOffset? createdAt = null)
    {
        return new WebhookAttempt(
            id: Guid.NewGuid(),
            jobId: jobId,
            attemptNumber: attemptNumber,
            requestedAt: requestedAt,
            responseStatusCode: responseStatusCode,
            durationMs: durationMs,
            nextRetryAt: nextRetryAt,
            failureReason: failureReason,
            createdAt: (createdAt ?? DateTimeOffset.UtcNow).ToUniversalTime());
    }

    public Guid Id { get; private set; }
    public Guid JobId { get; private set; }
    public int AttemptNumber { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public int? ResponseStatusCode { get; private set; }
    public long DurationMs { get; private set; }
    public DateTimeOffset? NextRetryAt { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
