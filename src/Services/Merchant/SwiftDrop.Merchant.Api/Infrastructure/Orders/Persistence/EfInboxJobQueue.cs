using Microsoft.EntityFrameworkCore;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Api.Infrastructure.Orders.Persistence;

/// <summary>
/// <c>inbox_jobs</c> work queue. On PostgreSQL, claiming uses
/// <c>FOR UPDATE SKIP LOCKED</c> inside a short transaction, so several
/// Merchant replicas can process concurrently without taking the same job.
/// Other providers (the in-memory test database) use an equivalent LINQ query.
/// </summary>
public sealed class EfInboxJobQueue(MerchantDbContext database) : IInboxJobQueue
{
    public async Task<IReadOnlyList<InboxJob>> ClaimDueAsync(
        int batchSize, TimeSpan leaseDuration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var leaseUntil = now + leaseDuration;

        if (!database.Database.IsNpgsql())
        {
            var due = await database.InboxJobs
                .Where(job => (job.State == InboxJobState.Accepted || job.State == InboxJobState.Retrying)
                    && job.NextRetryAt <= now)
                .OrderBy(job => job.NextRetryAt)
                .Take(batchSize)
                .ToListAsync(cancellationToken);
            return await LeaseAsync(due, leaseUntil, now, cancellationToken);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await database.InboxJobs
            .FromSqlInterpolated($"""
                SELECT * FROM inbox_jobs
                WHERE state IN ('Accepted', 'Retrying') AND next_retry_at <= {now}
                ORDER BY next_retry_at
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
        var leased = await LeaseAsync(claimed, leaseUntil, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return leased;
    }

    public async Task<IReadOnlyList<InboxJob>> ListDueForStatusRefreshAsync(
        int batchSize, DateTimeOffset staleBefore, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var terminal = ParcelStatuses.Terminal.ToArray();

        return await database.InboxJobs
            .Where(job => job.State == InboxJobState.Created
                && job.ParcelStatus != null && !terminal.Contains(job.ParcelStatus)
                && (job.LastRefreshedAt == null || job.LastRefreshedAt < staleBefore))
            .OrderBy(job => job.LastRefreshedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public Task SaveAsync(InboxJob job, CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);

    private async Task<IReadOnlyList<InboxJob>> LeaseAsync(
        List<InboxJob> jobs, DateTimeOffset leaseUntil, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var job in jobs)
        {
            job.Claim(leaseUntil, now);
        }

        if (jobs.Count > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
        }

        return jobs;
    }
}
