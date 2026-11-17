using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;
using SwiftDrop.Merchant.Api.Domain.Orders;

namespace SwiftDrop.Merchant.Api.Application.Orders.Processing;

/// <summary>
/// Keeps the dashboard's copy of parcel status current by polling Delivery's
/// <c>GetBookingByExternalOrder</c> for stale, non-terminal Created orders.
/// Forwards StatusChanged events to Merchant webhooks when parcel status advances.
/// <list type="bullet">
/// <item>Found → <c>ApplyStatus</c> (version-ordered, so a stale answer can never move a parcel backwards).</item>
/// <item>Any failure → left untouched, so <c>lastRefreshedAt</c> never claims freshness it does not have.</item>
/// <item>Unauthorized → stop the batch (credential problem).</item>
/// </list>
/// </summary>
public sealed class RefreshOrderStatusesHandler(
    IInboxJobQueue queue,
    IDeliveryBookingClient delivery,
    TimeProvider clock,
    IOptions<OrderProcessingOptions> options,
    ILogger<RefreshOrderStatusesHandler> logger,
    AcceptStatusChangedDispatchHandler? dispatchHandler = null)
{
    /// <returns>Number of orders checked.</returns>
    public async Task<int> RefreshBatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var staleBefore = clock.GetUtcNow() - settings.StatusRefreshAfter;
        var jobs = await queue.ListDueForStatusRefreshAsync(settings.StatusRefreshBatchSize, staleBefore, cancellationToken);

        foreach (var job in jobs)
        {
            var (_, stopBatch) = await RefreshJobAsync(job, cancellationToken);
            if (stopBatch)
            {
                break;
            }
        }

        return jobs.Count;
    }

    /// <summary>Refreshes a single order's parcel status from Delivery and triggers webhooks if changed.</summary>
    public async Task<(bool StatusChanged, bool StopBatch)> RefreshJobAsync(InboxJob job, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var result = await DeliveryCalls.WithDeadlineAsync(
            token => delivery.GetByExternalOrderAsync(job.BusinessId, job.ExternalOrderId, token),
            settings.DeliveryTimeout, logger, cancellationToken);

        switch (result)
        {
            case BookingCallResult.Found found:
                var statusChanged = job.ApplyStatus(found.Booking.ParcelStatus, clock.GetUtcNow());
                if (statusChanged)
                {
                    logger.LogInformation("Order job {JobId} parcel is now {ParcelStatus} (v{ParcelVersion}).",
                        job.Id, job.ParcelStatus, job.ParcelVersion);
                }

                await queue.SaveAsync(job, cancellationToken);

                if (statusChanged && dispatchHandler is not null && job.ParcelId.HasValue && !string.IsNullOrWhiteSpace(job.ParcelStatus))
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

                        var (response, failure) = await dispatchHandler.HandleAsync(payload, cancellationToken);
                        if (failure is not null)
                        {
                            logger.LogWarning("Webhook dispatch for order job {JobId} ({Status}) failed: {Failure}.",
                                job.Id, job.ParcelStatus, failure.Message);
                        }
                        else if (response is not null && response.JobsCreated > 0)
                        {
                            logger.LogInformation("Enqueued {JobsCreated} webhook job(s) for order {JobId} ({Status}).",
                                response.JobsCreated, job.Id, job.ParcelStatus);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error enqueueing webhook dispatch for order job {JobId}.", job.Id);
                    }
                }
                return (statusChanged, false);

            case BookingCallResult.Unauthorized unauthorized:
                logger.LogCritical("Delivery refused Merchant's service credential ({ErrorCode}); status refresh paused.",
                    unauthorized.ErrorCode);
                return (false, true);

            case BookingCallResult.NotFound:
                // Delivery confirmed this parcel earlier; losing it is an inconsistency to investigate, not to hide.
                logger.LogError("Delivery no longer reports parcel {ParcelId} for order job {JobId}.", job.ParcelId, job.Id);
                return (false, false);

            default:
                logger.LogWarning("Could not refresh order job {JobId}: {Result}.", job.Id, result);
                return (false, false);
        }
    }
}
