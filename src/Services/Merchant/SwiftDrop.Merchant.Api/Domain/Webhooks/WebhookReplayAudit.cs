namespace SwiftDrop.Merchant.Api.Domain.Webhooks;

public sealed class WebhookReplayAudit
{
    private WebhookReplayAudit() { }

    public WebhookReplayAudit(
        Guid id,
        Guid actorId,
        Guid businessId,
        Guid subscriptionId,
        Guid originalJobId,
        Guid replayJobId,
        string reason,
        DateTimeOffset createdAt)
    {
        Id = WebhookGuard.Id(id, nameof(id));
        ActorId = WebhookGuard.Id(actorId, nameof(actorId));
        BusinessId = WebhookGuard.Id(businessId, nameof(businessId));
        SubscriptionId = WebhookGuard.Id(subscriptionId, nameof(subscriptionId));
        OriginalJobId = WebhookGuard.Id(originalJobId, nameof(originalJobId));
        ReplayJobId = WebhookGuard.Id(replayJobId, nameof(replayJobId));
        Reason = string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException("Replay audit reason is required.", nameof(reason))
            : reason.Trim().Length > 500 ? reason.Trim()[..500] : reason.Trim();
        CreatedAt = createdAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid BusinessId { get; private set; }
    public Guid SubscriptionId { get; private set; }
    public Guid OriginalJobId { get; private set; }
    public Guid ReplayJobId { get; private set; }
    public string Reason { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
}
