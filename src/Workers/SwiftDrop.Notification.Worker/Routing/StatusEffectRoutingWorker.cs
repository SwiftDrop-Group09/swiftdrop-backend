using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Contracts.Http.V1;
using SwiftDrop.Notification.Worker.Persistence;

namespace SwiftDrop.Notification.Worker.Routing;

public sealed class StatusEffectRoutingWorker(
    IServiceScopeFactory scopes,
    IHttpClientFactory clients,
    ServiceJwtFileReader tokenFiles,
    NotificationProviderJobProcessor providerJobs,
    TimeProvider clock,
    ILogger<StatusEffectRoutingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)
    ];

    private bool deliveryTokenWarningWritten;
    private bool merchantTokenWarningWritten;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                didWork = await ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Status effect routing pass failed; durable jobs remain available for retry.");
            }

            try
            {
                await Task.Delay(didWork ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        var now = clock.GetUtcNow();
        var didWork = false;

        didWork = await providerJobs.ProcessDueAsync(db, now, cancellationToken) || didWork;

        var deliveryToken = await tokenFiles.ReadAsync("Delivery", cancellationToken);
        if (deliveryToken is null)
        {
            if (!deliveryTokenWarningWritten)
            {
                logger.LogWarning("Delivery routing is idle until a short-lived worker service token file is configured.");
                deliveryTokenWarningWritten = true;
            }
        }
        else
        {
            deliveryTokenWarningWritten = false;
            var ids = await db.NotificationJobs.AsNoTracking()
                .Where(job => job.State == "PendingRecipientResolution" &&
                    (job.NextAttemptAt == null || job.NextAttemptAt <= now) &&
                    (job.LeaseUntil == null || job.LeaseUntil < now))
                .OrderBy(job => job.CreatedAt)
                .Select(job => job.Id)
                .Take(16)
                .ToListAsync(cancellationToken);

            foreach (var id in ids)
            {
                didWork = await ResolveRecipientAsync(db, id, deliveryToken, cancellationToken) || didWork;
            }
        }

        var merchantToken = await tokenFiles.ReadAsync("Merchant", cancellationToken);
        if (merchantToken is null)
        {
            if (!merchantTokenWarningWritten)
            {
                logger.LogWarning("Merchant dispatch is idle until a short-lived worker service token file is configured.");
                merchantTokenWarningWritten = true;
            }
        }
        else
        {
            merchantTokenWarningWritten = false;
            var ids = await db.WebhookDispatchJobs.AsNoTracking()
                .Where(job => job.State == "Pending" &&
                    (job.NextAttemptAt == null || job.NextAttemptAt <= now) &&
                    (job.LeaseUntil == null || job.LeaseUntil < now))
                .OrderBy(job => job.CreatedAt)
                .Select(job => job.Id)
                .Take(16)
                .ToListAsync(cancellationToken);

            foreach (var id in ids)
            {
                didWork = await DispatchMerchantWebhookAsync(db, id, merchantToken, cancellationToken) || didWork;
            }
        }

        return didWork;
    }

    private async Task<bool> ResolveRecipientAsync(
        NotificationDbContext db,
        long id,
        string token,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var leaseUntil = now + LeaseDuration;
        var claimed = await db.NotificationJobs
            .Where(job => job.Id == id && job.State == "PendingRecipientResolution" &&
                (job.NextAttemptAt == null || job.NextAttemptAt <= now) &&
                (job.LeaseUntil == null || job.LeaseUntil < now))
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.LeaseUntil, leaseUntil), cancellationToken);
        if (claimed == 0)
        {
            return false;
        }

        var job = await db.NotificationJobs.SingleAsync(item => item.Id == id, cancellationToken);
        if (!Guid.TryParse(job.ParcelId, out var parcelId))
        {
            job.State = "Failed";
            job.LastErrorCode = "invalid_parcel_reference";
            job.LeaseUntil = null;
            job.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/notification-context");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new NotificationContextRequest(job.EventId, parcelId, job.ParcelVersion));
            using var response = await clients.CreateClient("DeliveryInternal").SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var result = await response.Content.ReadFromJsonAsync<NotificationContextResponse>(cancellationToken);
                if (result is null)
                {
                    await RetryNotificationResolutionAsync(db, job, now, "empty_delivery_context", cancellationToken);
                    return true;
                }

                job.RecipientId = result.CustomerUserId;
                job.State = result.CustomerUserId.HasValue ? "ReadyForProvider" : "NoRecipient";
                job.LastErrorCode = null;
                job.NextAttemptAt = null;
                job.LeaseUntil = null;
                job.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Resolved recipient context for notification job {JobId}", job.Id);
                return true;
            }

            await RetryNotificationResolutionAsync(db, job, now,
                $"delivery_http_{(int)response.StatusCode}", cancellationToken);
            return true;
        }
        catch (HttpRequestException)
        {
            await RetryNotificationResolutionAsync(db, job, now, "delivery_unavailable", cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RetryNotificationResolutionAsync(db, job, now, "delivery_timeout", cancellationToken);
            return true;
        }
    }

    private async Task RetryNotificationResolutionAsync(
        NotificationDbContext db,
        NotificationJob job,
        DateTimeOffset now,
        string errorCode,
        CancellationToken cancellationToken)
    {
        job.ResolutionAttempts++;
        job.LastErrorCode = errorCode;
        job.NextAttemptAt = now + RetryDelay(job.ResolutionAttempts);
        job.LeaseUntil = null;
        job.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Recipient resolution deferred for job {JobId} with code {ErrorCode}", job.Id, errorCode);
    }

    private async Task<bool> DispatchMerchantWebhookAsync(
        NotificationDbContext db,
        long id,
        string token,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var leaseUntil = now + LeaseDuration;
        var claimed = await db.WebhookDispatchJobs
            .Where(job => job.Id == id && job.State == "Pending" &&
                (job.NextAttemptAt == null || job.NextAttemptAt <= now) &&
                (job.LeaseUntil == null || job.LeaseUntil < now))
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.LeaseUntil, leaseUntil), cancellationToken);
        if (claimed == 0)
        {
            return false;
        }

        var job = await db.WebhookDispatchJobs.SingleAsync(item => item.Id == id, cancellationToken);
        if (!Guid.TryParse(job.ParcelId, out var parcelId) ||
            !Guid.TryParse(job.MerchantId, out var merchantId) ||
            job.ParcelVersion is < 1 or > int.MaxValue)
        {
            job.State = "Failed";
            job.LastErrorCode = "invalid_dispatch_reference";
            job.LeaseUntil = null;
            job.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogError("Webhook dispatch job {JobId} has an invalid event reference", job.Id);
            return true;
        }

        var payload = new InternalDispatchPayload(
            job.EventId,
            "StatusChanged",
            1,
            job.OccurredAt,
            parcelId,
            (int)job.ParcelVersion,
            merchantId,
            job.Status);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/webhook-dispatches");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(payload);
            using var response = await clients.CreateClient("MerchantInternal").SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                job.State = "Accepted";
                job.DispatchAttempts++;
                job.LastErrorCode = null;
                job.NextAttemptAt = null;
                job.LeaseUntil = null;
                job.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Merchant accepted webhook dispatch job {JobId} for event {EventId}", job.Id, job.EventId);
                return true;
            }

            await RetryWebhookDispatchAsync(db, job, now,
                $"merchant_http_{(int)response.StatusCode}", cancellationToken);
            return true;
        }
        catch (HttpRequestException)
        {
            await RetryWebhookDispatchAsync(db, job, now, "merchant_unavailable", cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RetryWebhookDispatchAsync(db, job, now, "merchant_timeout", cancellationToken);
            return true;
        }
    }

    private async Task RetryWebhookDispatchAsync(
        NotificationDbContext db,
        WebhookDispatchJob job,
        DateTimeOffset now,
        string errorCode,
        CancellationToken cancellationToken)
    {
        job.DispatchAttempts++;
        job.LastErrorCode = errorCode;
        job.NextAttemptAt = now + RetryDelay(job.DispatchAttempts);
        job.LeaseUntil = null;
        job.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Merchant dispatch deferred for job {JobId} with code {ErrorCode}", job.Id, errorCode);
    }

    private static TimeSpan RetryDelay(int attempt) => RetryDelays[Math.Clamp(attempt - 1, 0, RetryDelays.Length - 1)];

    private sealed record InternalDispatchPayload(
        Guid EventId,
        string EventType,
        int SchemaVersion,
        DateTimeOffset OccurredAt,
        Guid ParcelId,
        int ParcelVersion,
        Guid MerchantId,
        string Status);
}
