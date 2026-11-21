using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;

namespace SwiftDrop.Merchant.Api.Application.Orders.Processing;

/// <summary>One processor tick: claim due jobs, process each, save each immediately.</summary>
public sealed class ProcessDueOrdersHandler(
    IInboxJobQueue queue,
    InboxJobProcessor processor,
    TimeProvider clock,
    IOptions<OrderProcessingOptions> options,
    AcceptStatusChangedDispatchHandler? dispatchHandler = null,
    ILogger<ProcessDueOrdersHandler>? logger = null)
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

            if (outcome == JobOutcome.Created && dispatchHandler is not null && job.ParcelId.HasValue && !string.IsNullOrWhiteSpace(job.ParcelStatus))
            {
                try
                {
                    var payload = new InternalDispatchPayload(
                        EventId: Guid.NewGuid(),
                        EventType: "StatusChanged",
                        SchemaVersion: 1,
                        OccurredAt: job.LastStatusChangedAt ?? clock.GetUtcNow(),
                        ParcelId: job.ParcelId.Value,
                        ParcelVersion: (int)(job.ParcelVersion ?? 1),
                        MerchantId: job.BusinessId,
                        Status: job.ParcelStatus);

                    await dispatchHandler.HandleAsync(payload, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error enqueueing webhook dispatch for created order job {JobId}.", job.Id);
                }
            }

            if (outcome == JobOutcome.DeliveryUnauthorized)
            {
                // Every remaining call would fail the same way; unprocessed jobs stay leased and return when the lease expires.
                break;
            }
        }

        return jobs.Count;
    }
}
