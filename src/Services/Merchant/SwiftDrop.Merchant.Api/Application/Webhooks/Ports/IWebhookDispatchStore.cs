using SwiftDrop.Merchant.Api.Domain.Webhooks;

namespace SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

public interface IWebhookDispatchStore
{
    Task AddJobsAsync(IEnumerable<WebhookDeliveryJob> jobs, CancellationToken ct);
    Task<WebhookDeliveryJob?> GetJobByIdAsync(Guid jobId, CancellationToken ct);
    Task<WebhookDeliveryJob?> GetJobBySubscriptionAndEventAsync(Guid subscriptionId, Guid eventId, int replayGeneration, CancellationToken ct);
    Task<WebhookDeliveryJob?> GetLatestJobByEventAsync(Guid subscriptionId, Guid eventId, CancellationToken ct);
    Task UpdateJobAsync(WebhookDeliveryJob job, CancellationToken ct);
    Task CancelUnsentJobsBySubscriptionAsync(Guid subscriptionId, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<WebhookDeliveryJob>> ClaimDueJobsAsync(int batchSize, TimeSpan leaseDuration, Guid leaseToken, DateTimeOffset now, CancellationToken ct);
    Task RecordAttemptAsync(WebhookAttempt attempt, CancellationToken ct);
    Task<IReadOnlyList<WebhookAttempt>> ListAttemptsByJobAsync(Guid jobId, int skip, int take, CancellationToken ct);
    Task<IReadOnlyList<WebhookAttempt>> ListAttemptsBySubscriptionAsync(Guid subscriptionId, Guid? jobId, int skip, int take, CancellationToken ct);
    Task<int> CountAttemptsBySubscriptionAsync(Guid subscriptionId, Guid? jobId, CancellationToken ct);
    Task RecordReplayAuditAsync(WebhookReplayAudit audit, CancellationToken ct);
}
