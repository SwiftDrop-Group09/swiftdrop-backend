using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Domain.Webhooks;

namespace SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;

public sealed class ProcessDueWebhookJobsHandler(
    IWebhookDispatchStore dispatchStore,
    IWebhookSubscriptionStore subscriptionStore,
    IWebhookSecretProtector secretProtector,
    IWebhookDestinationValidator destinationValidator,
    IWebhookSender sender,
    IWebhookClock clock)
{
    public async Task<int> ProcessBatchAsync(
        int batchSize,
        TimeSpan leaseDuration,
        Guid leaseToken,
        CancellationToken ct)
    {
        var now = clock.UtcNow;
        var claimedJobs = await dispatchStore.ClaimDueJobsAsync(batchSize, leaseDuration, leaseToken, now, ct);
        if (claimedJobs.Count == 0)
        {
            return 0;
        }

        var processedCount = 0;

        foreach (var job in claimedJobs)
        {
            var subscription = await subscriptionStore.GetByIdAsync(job.SubscriptionId, ct);
            if (subscription is null || !subscription.IsActive)
            {
                job.Cancel(clock.UtcNow);
                await dispatchStore.UpdateJobAsync(job, ct);
                processedCount++;
                continue;
            }

            byte[] rawSecret;
            try
            {
                rawSecret = secretProtector.Decrypt(
                    subscription.SecretCiphertext,
                    subscription.SecretNonce,
                    subscription.SecretTag,
                    subscription.KeyVersion);
            }
            catch (Exception)
            {
                var failureTime = clock.UtcNow;
                job.RecordAttempt(delivered: false, nextRetryAt: null, safeErrorCode: "crypto_decrypt_failure", failureTime);
                var attempt = WebhookAttempt.Record(
                    job.Id,
                    job.AttemptCount,
                    failureTime,
                    responseStatusCode: null,
                    durationMs: 0,
                    nextRetryAt: null,
                    failureReason: "Failed to decrypt webhook signing secret.",
                    createdAt: failureTime);

                await dispatchStore.RecordAttemptAsync(attempt, ct);
                await dispatchStore.UpdateJobAsync(job, ct);
                processedCount++;
                continue;
            }

            var isSafe = await destinationValidator.ValidateAsync(subscription.EndpointUrl, ct);
            if (!isSafe)
            {
                var failureTime = clock.UtcNow;
                job.RecordAttempt(delivered: false, nextRetryAt: null, safeErrorCode: "unsafe_destination", failureTime);
                var attempt = WebhookAttempt.Record(
                    job.Id,
                    job.AttemptCount,
                    failureTime,
                    responseStatusCode: null,
                    durationMs: 0,
                    nextRetryAt: null,
                    failureReason: "Destination validation rejected: unsafe destination or blocked IP.",
                    createdAt: failureTime);

                await dispatchStore.RecordAttemptAsync(attempt, ct);
                await dispatchStore.UpdateJobAsync(job, ct);
                processedCount++;
                continue;
            }

            var requestedAt = clock.UtcNow;
            var sendResult = await sender.SendAsync(
                subscription.EndpointUrl,
                job.FrozenEventBytes,
                rawSecret,
                job.EventId.ToString(),
                job.SecretVersion,
                ct);

            var completedAt = clock.UtcNow;

            if (sendResult.Success)
            {
                job.RecordAttempt(delivered: true, nextRetryAt: null, safeErrorCode: null, completedAt);
                var attempt = WebhookAttempt.Record(
                    job.Id,
                    job.AttemptCount,
                    requestedAt,
                    sendResult.StatusCode,
                    sendResult.DurationMs,
                    nextRetryAt: null,
                    failureReason: null,
                    createdAt: completedAt);

                await dispatchStore.RecordAttemptAsync(attempt, ct);
                await dispatchStore.UpdateJobAsync(job, ct);
            }
            else
            {
                var nextAttemptNumber = job.AttemptCount + 1 + 1;
                DateTimeOffset? nextRetryAt = null;

                if (sendResult.CanRetry && nextAttemptNumber <= WebhookRetryPolicy.MaxAttempts)
                {
                    var delay = WebhookRetryPolicy.CalculateDelay(nextAttemptNumber, sendResult.RetryAfter);
                    nextRetryAt = requestedAt.Add(delay);
                }

                var safeErrorCode = sendResult.StatusCode.HasValue
                    ? $"http_{sendResult.StatusCode.Value}"
                    : "network_failure";

                job.RecordAttempt(delivered: false, nextRetryAt: nextRetryAt, safeErrorCode: safeErrorCode, completedAt);
                var attempt = WebhookAttempt.Record(
                    job.Id,
                    job.AttemptCount,
                    requestedAt,
                    sendResult.StatusCode,
                    sendResult.DurationMs,
                    nextRetryAt: nextRetryAt,
                    failureReason: sendResult.FailureReason,
                    createdAt: completedAt);

                await dispatchStore.RecordAttemptAsync(attempt, ct);
                await dispatchStore.UpdateJobAsync(job, ct);
            }

            processedCount++;
        }

        return processedCount;
    }
}
