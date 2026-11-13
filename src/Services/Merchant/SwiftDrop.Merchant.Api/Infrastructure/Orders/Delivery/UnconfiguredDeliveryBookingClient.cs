using SwiftDrop.Merchant.Api.Application.Orders.Ports;

namespace SwiftDrop.Merchant.Api.Infrastructure.Orders.Delivery;

/// <summary>
/// Default when no Delivery client is configured. Every call is a transient
/// <c>delivery_not_configured</c>, so orders stay safely Retrying (never a
/// false Created) and complete once a real client is configured.
/// </summary>
public sealed class UnconfiguredDeliveryBookingClient : IDeliveryBookingClient
{
    private static readonly Task<BookingCallResult> NotConfigured =
        Task.FromResult<BookingCallResult>(new BookingCallResult.Transient(DeliveryErrorCodes.NotConfigured));

    public Task<BookingCallResult> CreateBookingAsync(CreateBookingCommand command, CancellationToken cancellationToken) => NotConfigured;

    public Task<BookingCallResult> GetByExternalOrderAsync(Guid merchantId, string externalOrderId, CancellationToken cancellationToken) =>
        NotConfigured;
}
