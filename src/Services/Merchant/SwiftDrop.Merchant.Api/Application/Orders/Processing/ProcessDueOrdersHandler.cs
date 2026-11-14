using Microsoft.Extensions.Options;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;

namespace SwiftDrop.Merchant.Api.Application.Orders.Processing;

/// <summary>One processor tick: claim due jobs, process each, save each immediately.</summary>
public sealed class ProcessDueOrdersHandler(
    IInboxJobQueue queue,
    InboxJobProcessor processor,
    TimeProvider clock,
    IOptions<OrderProcessingOptions> options)
{
    /// <returns>Number of jobs claimed (0 means nothing was due).</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var jobs = await queue.ClaimDueAsync(settings.BatchSize, settings.LeaseDuration, clock.GetUtcNow(), cancellationToken);

        foreach (var job in jobs)
        {
            var outcome = await processor.ProcessAsync(job, cancellationToken);

            // Save per job: a crash later in the batch never loses an outcome already received from Delivery.
            await queue.SaveAsync(job, cancellationToken);

            if (outcome == JobOutcome.DeliveryUnauthorized)
            {
                // Every remaining call would fail the same way; unprocessed jobs stay leased and return when the lease expires.
                break;
            }
        }

        return jobs.Count;
    }
}
