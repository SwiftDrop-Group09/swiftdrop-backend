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
        var claimedJobs = await dispatchStore.ClaimDueJobsAsync(batchSize, leaseDuration, leaseToken, clock.UtcNow, ct);
        var processedCount = 0;
        foreach (var job in claimedJobs)
        {
            if (job.LeasedUntil <= clock.UtcNow)
            {
                continue;
            }

            if (await ProcessJobAsync(job, ct))
            {
                processedCount++;
            }
        }

        return processedCount;
    }

    private async Task<bool> ProcessJobAsync(WebhookDeliveryJob job, CancellationToken ct)
    {
        var subscription = await subscriptionStore.GetByIdAsync(job.SubscriptionId, ct);
        if (subscription is null || !subscription.IsActive)
        {
            job.Cancel(clock.UtcNow);
            return await dispatchStore.CompleteClaimAsync(job, null, ct);
        }

        byte[] rawSecret;
        try
        {
            rawSecret = secretProtector.Decrypt(subscription.SecretCiphertext, subscription.SecretNonce,
                subscription.SecretTag, subscription.KeyVersion);
        }
        catch (Exception)
        {
            return await FailAsync(job, "crypto_decrypt_failure", "Failed to decrypt webhook signing secret.", ct);
        }

        if (!await destinationValidator.ValidateAsync(subscription.EndpointUrl, ct))
        {
            return await FailAsync(job, "unsafe_destination", "Destination validation rejected: unsafe destination or blocked IP.", ct);
        }

        var requestedAt = clock.UtcNow;
        if (job.LeasedUntil <= requestedAt)
        {
            return false;
        }

        var result = await sender.SendAsync(subscription.EndpointUrl, job.FrozenEventBytes, rawSecret,
            job.EventId.ToString(), job.SecretVersion, ct);
        var completedAt = clock.UtcNow;
        var nextAttemptNumber = job.AttemptCount + 2;
        DateTimeOffset? nextRetryAt = null;
        if (!result.Success && result.CanRetry && nextAttemptNumber <= WebhookRetryPolicy.MaxAttempts)
        {
            nextRetryAt = requestedAt.Add(WebhookRetryPolicy.CalculateDelay(nextAttemptNumber, result.RetryAfter));
        }

        var safeErrorCode = result.Success ? null
            : result.StatusCode is int status ? $"http_{status}" : "network_failure";
        job.RecordAttempt(result.Success, nextRetryAt, safeErrorCode, completedAt);
        var attempt = WebhookAttempt.Record(job.Id, job.AttemptCount, requestedAt, result.StatusCode,
            result.DurationMs, nextRetryAt, result.FailureReason, completedAt);
        return await dispatchStore.CompleteClaimAsync(job, attempt, ct);
    }

    private Task<bool> FailAsync(WebhookDeliveryJob job, string code, string reason, CancellationToken ct)
    {
        var now = clock.UtcNow;
        job.RecordAttempt(delivered: false, nextRetryAt: null, safeErrorCode: code, now);
        var attempt = WebhookAttempt.Record(job.Id, job.AttemptCount, now, responseStatusCode: null,
            durationMs: 0, nextRetryAt: null, failureReason: reason, createdAt: now);
        return dispatchStore.CompleteClaimAsync(job, attempt, ct);
    }
}
