using Microsoft.Extensions.Options;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;

namespace SwiftDrop.Merchant.Api.Application.Orders.Processing;

/// <summary>
/// Keeps the dashboard's copy of parcel status current by polling Delivery's
/// <c>GetBookingByExternalOrder</c> for stale, non-terminal Created orders.
/// Needed until the Notification Worker forwards StatusChanged events to Merchant.
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
    ILogger<RefreshOrderStatusesHandler> logger)
{
    /// <returns>Number of orders checked.</returns>
    public async Task<int> RefreshBatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var staleBefore = clock.GetUtcNow() - settings.StatusRefreshAfter;
        var jobs = await queue.ListDueForStatusRefreshAsync(settings.StatusRefreshBatchSize, staleBefore, cancellationToken);

        foreach (var job in jobs)
        {
            var result = await DeliveryCalls.WithDeadlineAsync(
                token => delivery.GetByExternalOrderAsync(job.BusinessId, job.ExternalOrderId, token),
                settings.DeliveryTimeout, logger, cancellationToken);

            switch (result)
            {
                case BookingCallResult.Found found:
                    if (job.ApplyStatus(found.Booking.ParcelStatus, clock.GetUtcNow()))
                    {
                        logger.LogInformation("Order job {JobId} parcel is now {ParcelStatus} (v{ParcelVersion}).",
                            job.Id, job.ParcelStatus, job.ParcelVersion);
                    }

                    await queue.SaveAsync(job, cancellationToken);
                    break;

                case BookingCallResult.Unauthorized unauthorized:
                    logger.LogCritical("Delivery refused Merchant's service credential ({ErrorCode}); status refresh paused.",
                        unauthorized.ErrorCode);
                    return jobs.Count;

                case BookingCallResult.NotFound:
                    // Delivery confirmed this parcel earlier; losing it is an inconsistency to investigate, not to hide.
                    logger.LogError("Delivery no longer reports parcel {ParcelId} for order job {JobId}.", job.ParcelId, job.Id);
                    break;

                default:
                    logger.LogWarning("Could not refresh order job {JobId}: {Result}.", job.Id, result);
                    break;
            }
        }

        return jobs.Count;
    }
}
