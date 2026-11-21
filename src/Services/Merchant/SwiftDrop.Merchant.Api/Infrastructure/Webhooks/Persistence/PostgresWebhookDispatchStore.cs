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

    public async Task<int> AddAutomaticJobsAsync(IReadOnlyList<WebhookDeliveryJob> jobs, CancellationToken ct)
    {
        if (jobs.Count == 0)
        {
            return 0;
        }

        if (dbContext.Database.IsNpgsql())
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            var inserted = 0;
            foreach (var job in jobs.OrderBy(job => job.SubscriptionId))
            {
                inserted += await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO webhook_dispatch_jobs
                        (id, event_id, subscription_id, replay_generation, frozen_event_bytes, secret_version,
                         status, attempt_count, due_at, created_at, updated_at)
                    VALUES ({job.Id}, {job.EventId}, {job.SubscriptionId}, {job.ReplayGeneration},
                            {job.FrozenEventBytes}, {job.SecretVersion}, {job.Status.ToString()},
                            {job.AttemptCount}, {job.DueAt}, {job.CreatedAt}, {job.UpdatedAt})
                    ON CONFLICT (event_id, subscription_id, replay_generation) DO NOTHING
                    """, ct);
            }

            await transaction.CommitAsync(ct);
            return inserted;
        }

        var pending = new List<WebhookDeliveryJob>();
        foreach (var job in jobs)
        {
            var exists = await dbContext.WebhookDispatchJobs.AnyAsync(candidate =>
                candidate.SubscriptionId == job.SubscriptionId && candidate.EventId == job.EventId &&
                candidate.ReplayGeneration == job.ReplayGeneration, ct);
            if (!exists)
            {
                pending.Add(job);
            }
        }

        await AddJobsAsync(pending, ct);
        return pending.Count;
    }

    public async Task<WebhookDeliveryJob?> GetJobByIdAsync(Guid jobId, CancellationToken ct)
    {
        return await dbContext.WebhookDispatchJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
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

    public async Task CancelUnsentJobsBySubscriptionAsync(Guid subscriptionId, DateTimeOffset now, CancellationToken ct)
    {
        var candidates = dbContext.WebhookDispatchJobs
            .Where(j => j.SubscriptionId == subscriptionId &&
                (j.Status == WebhookStatus.Pending || j.Status == WebhookStatus.PendingResolution));

        if (dbContext.Database.IsNpgsql())
        {
            var updatedAt = now.ToUniversalTime();
            await candidates.ExecuteUpdateAsync(update => update
                .SetProperty(j => j.Status, WebhookStatus.Cancelled)
                .SetProperty(j => j.LeasedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.LeaseToken, (Guid?)null)
                .SetProperty(j => j.UpdatedAt, updatedAt), ct);
            return;
        }

        var unsentJobs = await candidates.ToListAsync(ct);
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
                    WHERE (leased_until IS NULL OR leased_until <= {now})
                        AND ((status = 'Pending' AND due_at <= {now}) OR status = 'Processing')
                    ORDER BY due_at, id
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
                .Where(j => (j.LeasedUntil == null || j.LeasedUntil <= now) &&
                    ((j.Status == WebhookStatus.Pending && j.DueAt <= now) || j.Status == WebhookStatus.Processing))
                .OrderBy(j => j.DueAt).ThenBy(j => j.Id)
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

    public async Task<bool> CompleteClaimAsync(WebhookDeliveryJob job, WebhookAttempt? attempt, CancellationToken ct)
    {
        var entry = dbContext.Entry(job);
        if (entry.State == EntityState.Detached || entry.Property(x => x.LeaseToken).OriginalValue is null)
        {
            throw new InvalidOperationException("Webhook completion requires a job claimed by this store.");
        }

        if (attempt is not null)
        {
            dbContext.WebhookAttempts.Add(attempt);
        }

        try
        {
            // EF predicates the update on the original lease token and commits it with the attempt insert.
            await dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException exception) when (exception.Entries.Any(x => ReferenceEquals(x.Entity, job)))
        {
            if (attempt is not null)
            {
                dbContext.Entry(attempt).State = EntityState.Detached;
            }

            entry.State = EntityState.Detached;
            return false;
        }
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
