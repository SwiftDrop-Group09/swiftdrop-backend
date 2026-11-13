using SwiftDrop.Merchant.Api.Domain.Orders;

namespace SwiftDrop.Merchant.Api.Application.Orders.Ports;

/// <summary>Durable work queue over <c>inbox_jobs</c>.</summary>
public interface IInboxJobQueue
{
    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> due Accepted/Retrying
    /// jobs and leases them until <c>now + leaseDuration</c>. Concurrent
    /// processors never receive the same job.
    /// </summary>
    Task<IReadOnlyList<InboxJob>> ClaimDueAsync(int batchSize, TimeSpan leaseDuration, DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Persists the job's new state after processing.</summary>
    Task SaveAsync(InboxJob job, CancellationToken cancellationToken);
}
