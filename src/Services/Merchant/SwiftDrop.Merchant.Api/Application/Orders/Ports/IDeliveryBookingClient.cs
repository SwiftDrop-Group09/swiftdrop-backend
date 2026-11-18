namespace SwiftDrop.Merchant.Api.Application.Orders.Ports;

/// <summary>
/// Merchant's only way to reach Delivery's booking RPCs. Implementations:
/// the development fake now, the gRPC client (with service JWT) later.
/// Implementations translate transport errors into <see cref="BookingCallResult"/>
/// and must not throw for expected failures.
/// </summary>
public interface IDeliveryBookingClient
{
    /// <summary>CreateBooking: create the parcel, or return the existing one for the same order and payload.</summary>
    Task<BookingCallResult> CreateBookingAsync(CreateBookingCommand command, CancellationToken cancellationToken);

    /// <summary>GetBookingByExternalOrder: <see cref="BookingCallResult.NotFound"/> only when Delivery definitely has no parcel.</summary>
    Task<BookingCallResult> GetByExternalOrderAsync(Guid merchantId, string externalOrderId, CancellationToken cancellationToken);
}
