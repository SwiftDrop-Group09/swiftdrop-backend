using SwiftDrop.Merchant.Api.Application.Orders.Ports;

namespace SwiftDrop.Merchant.Api.Application.Orders.Processing;

/// <summary>Shared guard for every call Merchant makes to Delivery.</summary>
public static class DeliveryCalls
{
    /// <summary>
    /// Runs one Delivery call under <paramref name="timeout"/>. A deadline hit is a
    /// transient <c>delivery_timeout</c> (unknown outcome) and an unexpected
    /// exception a transient <c>delivery_error</c>; host shutdown is not swallowed.
    /// </summary>
    public static async Task<BookingCallResult> WithDeadlineAsync(
        Func<CancellationToken, Task<BookingCallResult>> call,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
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
