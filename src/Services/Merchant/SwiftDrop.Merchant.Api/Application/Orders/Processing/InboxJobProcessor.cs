using System.Text.Json;
using Microsoft.Extensions.Options;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Domain.Orders;

namespace SwiftDrop.Merchant.Api.Application.Orders.Processing;

/// <summary>What happened to one job in one processing attempt.</summary>
public enum JobOutcome
{
    Created,
    Retrying,
    Failed,

    /// <summary>Delivery refused Merchant's credential; the job is kept and the batch should stop.</summary>
    DeliveryUnauthorized
}

/// <summary>
/// Moves one claimed job forward by talking to Delivery:
/// <list type="number">
/// <item><b>Reconcile first.</b> Ask Delivery whether the order already has a parcel.
/// An earlier attempt may have succeeded even though its response was lost;
/// an existing parcel always wins, so a duplicate is never created.</item>
/// <item><b>Create only on a definite NotFound</b>, with the same key and fingerprint as every earlier attempt.</item>
/// <item><b>Map the result</b>: Found → Created; Rejected → Failed; Transient → Retrying with backoff;
/// Unauthorized → Retrying and stop the batch (configuration problem).</item>
/// </list>
/// Each Delivery call has its own deadline; a timeout is an unknown outcome, never a failure.
/// </summary>
public sealed class InboxJobProcessor(
    IDeliveryBookingClient delivery,
    TimeProvider clock,
    IOptions<OrderProcessingOptions> options,
    ILogger<InboxJobProcessor> logger)
{
    public async Task<JobOutcome> ProcessAsync(InboxJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        CreateBookingCommand command;
        try
        {
            command = CreateBookingCommand.FromJob(job);
        }
        catch (JsonException)
        {
            // Payloads are validated at intake, so this means stored data was damaged; retrying cannot help.
            job.MarkFailed(DeliveryErrorCodes.InvalidStoredOrder, clock.GetUtcNow());
            logger.LogError("Order job {JobId} has an unreadable stored payload and was marked Failed.", job.Id);
            return JobOutcome.Failed;
        }

        var existing = await CallDeliveryAsync(
            token => delivery.GetByExternalOrderAsync(job.BusinessId, job.ExternalOrderId, token), cancellationToken);
        if (existing is not BookingCallResult.NotFound)
        {
            return Apply(job, existing);
        }

        var created = await CallDeliveryAsync(token => delivery.CreateBookingAsync(command, token), cancellationToken);
        return Apply(job, created);
    }

    private JobOutcome Apply(InboxJob job, BookingCallResult result)
    {
        var now = clock.GetUtcNow();
        switch (result)
        {
            case BookingCallResult.Found found:
                job.MarkCreated(found.Booking, now);
                logger.LogInformation("Order job {JobId} created parcel {ParcelId}.", job.Id, found.Booking.ParcelId);
                return JobOutcome.Created;

            case BookingCallResult.Rejected rejected:
                job.MarkFailed(rejected.ErrorCode, now);
                logger.LogWarning("Order job {JobId} was rejected by Delivery: {ErrorCode}.", job.Id, rejected.ErrorCode);
                return JobOutcome.Failed;

            case BookingCallResult.Unauthorized unauthorized:
                job.ScheduleRetry(unauthorized.ErrorCode, now);
                logger.LogCritical("Delivery refused Merchant's service credential ({ErrorCode}); order processing paused until it is fixed.",
                    unauthorized.ErrorCode);
                return JobOutcome.DeliveryUnauthorized;

            case BookingCallResult.Transient transient:
                return ScheduleRetry(job, transient.ErrorCode, now);

            default:
                // NotFound from CreateBooking is not a valid answer; treat it as an unknown outcome.
                return ScheduleRetry(job, DeliveryErrorCodes.UnexpectedError, now);
        }
    }

    private JobOutcome ScheduleRetry(InboxJob job, string errorCode, DateTimeOffset now)
    {
        job.ScheduleRetry(errorCode, now);
        if (InboxJobRetryPolicy.ShouldAlert(job.Attempts))
        {
            logger.LogError("ALERT: order job {JobId} has failed {Attempts} times (latest {ErrorCode}); still retrying at {NextRetryAt}.",
                job.Id, job.Attempts, errorCode, job.NextRetryAt);
        }
        else
        {
            logger.LogWarning("Order job {JobId} will retry at {NextRetryAt} after {ErrorCode}.", job.Id, job.NextRetryAt, errorCode);
        }

        return JobOutcome.Retrying;
    }

    /// <summary>Runs one Delivery call under the configured deadline; timeouts and unexpected errors become Transient.</summary>
    private async Task<BookingCallResult> CallDeliveryAsync(
        Func<CancellationToken, Task<BookingCallResult>> call, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Value.DeliveryTimeout);
        try
        {
            return await call(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BookingCallResult.Transient(DeliveryErrorCodes.Timeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Unexpected error calling Delivery.");
            return new BookingCallResult.Transient(DeliveryErrorCodes.UnexpectedError);
        }
    }
}
