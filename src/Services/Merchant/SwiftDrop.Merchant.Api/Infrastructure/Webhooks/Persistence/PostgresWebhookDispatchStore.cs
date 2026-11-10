using Microsoft.EntityFrameworkCore;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Domain.Webhooks;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Persistence;

public sealed class PostgresWebhookDispatchStore(MerchantDbContext dbContext) : IWebhookDispatchStore
{
    public async Task AddJobsAsync(IEnumerable<WebhookDeliveryJob> jobs, CancellationToken ct)
    {
        dbContext.WebhookDispatchJobs.AddRange(jobs);
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task<WebhookDeliveryJob?> GetJobByIdAsync(Guid jobId, CancellationToken ct)
    {
        return await dbContext.WebhookDispatchJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
    }

    public async Task<WebhookDeliveryJob?> GetJobBySubscriptionAndEventAsync(
        Guid subscriptionId,
        Guid eventId,
        int replayGeneration,
        CancellationToken ct)
    {
        return await dbContext.WebhookDispatchJobs
            .FirstOrDefaultAsync(j =>
                j.SubscriptionId == subscriptionId &&
                j.EventId == eventId &&
                j.ReplayGeneration == replayGeneration,
                ct);
    }

    public async Task<WebhookDeliveryJob?> GetLatestJobByEventAsync(
        Guid subscriptionId,
        Guid eventId,
        CancellationToken ct)
    {
        return await dbContext.WebhookDispatchJobs
            .Where(j => j.SubscriptionId == subscriptionId && j.EventId == eventId)
            .OrderByDescending(j => j.ReplayGeneration)
            .FirstOrDefaultAsync(ct);
    }

    public async Task UpdateJobAsync(WebhookDeliveryJob job, CancellationToken ct)
    {
        dbContext.WebhookDispatchJobs.Update(job);
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task CancelUnsentJobsBySubscriptionAsync(Guid subscriptionId, DateTimeOffset now, CancellationToken ct)
    {
        var unsentJobs = await dbContext.WebhookDispatchJobs
            .Where(j => j.SubscriptionId == subscriptionId &&
                (j.Status == WebhookStatus.Pending || j.Status == WebhookStatus.PendingResolution))
            .ToListAsync(ct);

        foreach (var job in unsentJobs)
        {
            job.Cancel(now);
        }

        if (unsentJobs.Count > 0)
        {
            await dbContext.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<WebhookDeliveryJob>> ClaimDueJobsAsync(
        int batchSize,
        TimeSpan leaseDuration,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var leasedUntil = now.Add(leaseDuration);

        if (dbContext.Database.IsNpgsql())
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

            var claimed = await dbContext.WebhookDispatchJobs
                .FromSqlInterpolated($@"
                    SELECT * FROM webhook_dispatch_jobs
                    WHERE status = 'Pending' AND due_at <= {now} AND (leased_until IS NULL OR leased_until < {now})
                    ORDER BY due_at ASC
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED")
                .ToListAsync(ct);

            foreach (var job in claimed)
            {
                job.Claim(leasedUntil, leaseToken, now);
            }

            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return claimed;
        }
        else
        {
            var claimed = await dbContext.WebhookDispatchJobs
                .Where(j => j.Status == WebhookStatus.Pending &&
                    j.DueAt <= now &&
                    (j.LeasedUntil == null || j.LeasedUntil < now))
                .OrderBy(j => j.DueAt)
                .Take(batchSize)
                .ToListAsync(ct);

            foreach (var job in claimed)
            {
                job.Claim(leasedUntil, leaseToken, now);
            }

            if (claimed.Count > 0)
            {
                await dbContext.SaveChangesAsync(ct);
            }

            return claimed;
        }
    }

    public async Task RecordAttemptAsync(WebhookAttempt attempt, CancellationToken ct)
    {
        dbContext.WebhookAttempts.Add(attempt);
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WebhookAttempt>> ListAttemptsByJobAsync(Guid jobId, int skip, int take, CancellationToken ct)
    {
        return await dbContext.WebhookAttempts
            .AsNoTracking()
            .Where(a => a.JobId == jobId)
            .OrderByDescending(a => a.AttemptNumber)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WebhookAttempt>> ListAttemptsBySubscriptionAsync(
        Guid subscriptionId,
        Guid? jobId,
        int skip,
        int take,
        CancellationToken ct)
    {
        var query = dbContext.WebhookAttempts
            .AsNoTracking()
            .Where(a => dbContext.WebhookDispatchJobs.Any(j => j.Id == a.JobId && j.SubscriptionId == subscriptionId));

        if (jobId.HasValue)
        {
            query = query.Where(a => a.JobId == jobId.Value);
        }

        return await query
            .OrderByDescending(a => a.RequestedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<int> CountAttemptsBySubscriptionAsync(Guid subscriptionId, Guid? jobId, CancellationToken ct)
    {
        var query = dbContext.WebhookAttempts
            .AsNoTracking()
            .Where(a => dbContext.WebhookDispatchJobs.Any(j => j.Id == a.JobId && j.SubscriptionId == subscriptionId));

        if (jobId.HasValue)
        {
            query = query.Where(a => a.JobId == jobId.Value);
        }

        return await query.CountAsync(ct);
    }

    public async Task RecordReplayAuditAsync(WebhookReplayAudit audit, CancellationToken ct)
    {
        dbContext.WebhookReplayAudits.Add(audit);
        await dbContext.SaveChangesAsync(ct);
    }
}
