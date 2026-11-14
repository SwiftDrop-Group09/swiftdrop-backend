using System.Collections.Concurrent;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Domain.Orders;

namespace SwiftDrop.Merchant.Api.Infrastructure.Orders.Delivery;

/// <summary>
/// DEVELOPMENT/TEST ONLY stand-in for Delivery's booking RPCs, until the real
/// gRPC server exists. It behaves like the contract (same order + payload →
/// same parcel; changed payload → order_conflict) but keeps bookings in memory,
/// so they disappear on restart and are not real Delivery parcels.
/// Registration refuses to use it outside Development/Testing.
/// </summary>
public sealed class FakeDeliveryBookingClient(TimeProvider clock) : IDeliveryBookingClient
{
    // Example seed tariff from the API contract: base 450 + 60 per kg above 1 kg.
    private const decimal BaseFee = 450m;
    private const decimal PerKgFee = 60m;

    private readonly ConcurrentDictionary<(Guid MerchantId, string ExternalOrderId), (string Fingerprint, BookingConfirmation Booking)> _bookings = new();

    public Task<BookingCallResult> CreateBookingAsync(CreateBookingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        var key = (command.MerchantId, command.ExternalOrderId);
        var stored = _bookings.GetOrAdd(key, _ => (command.RequestFingerprint, NewBooking(command)));

        BookingCallResult result = stored.Fingerprint == command.RequestFingerprint
            ? new BookingCallResult.Found(stored.Booking)
            : new BookingCallResult.Rejected(DeliveryErrorCodes.OrderConflict);
        return Task.FromResult(result);
    }

    public Task<BookingCallResult> GetByExternalOrderAsync(Guid merchantId, string externalOrderId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BookingCallResult result = _bookings.TryGetValue((merchantId, externalOrderId), out var stored)
            ? new BookingCallResult.Found(stored.Booking)
            : new BookingCallResult.NotFound();
        return Task.FromResult(result);
    }

    private BookingConfirmation NewBooking(CreateBookingCommand command)
    {
        var now = clock.GetUtcNow();
        var weight = decimal.Parse(command.WeightKg, System.Globalization.CultureInfo.InvariantCulture);
        var total = decimal.Round(BaseFee + PerKgFee * Math.Max(0m, weight - 1m), 2, MidpointRounding.AwayFromZero);

        return new BookingConfirmation(
            parcelId: Guid.NewGuid(),
            trackingNumber: $"SD-DEV-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            parcelStatus: new ParcelStatusSnapshot(ParcelStatuses.Created, version: 1, changedAt: now),
            total: total,
            currency: "LKR",
            pricingVersion: command.AcceptedPriceVersion);
    }
}
