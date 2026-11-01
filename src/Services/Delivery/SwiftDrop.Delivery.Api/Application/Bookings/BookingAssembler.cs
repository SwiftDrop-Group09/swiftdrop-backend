using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Bookings;

/// <summary>Builds persistence records after an entry adapter has authorized parcel creation.</summary>
public static class BookingAssembler
{
    /// <summary>Creates records after the Customer adapter has authorized its owner-bound quote.</summary>
    public static BookingWriteSet CreateCustomer(Guid customerId, ParcelDetails details, PricingTier tier,
        PriceBreakdown price, ITrackingNumberGenerator trackingNumbers, DateTimeOffset createdAt, string correlationId)
    {
        var parcel = Parcel.ForCustomer(Guid.NewGuid(), customerId, trackingNumbers.Generate(), details, createdAt);
        return Create(parcel, tier, price, customerId, correlationId);
    }

    /// <summary>
    /// Creates records for an already authorized Merchant workload/business context.
    /// The entry adapter owns business binding, zone/tariff lookup and durable deduplication.
    /// actorId is a trusted audit identity, never an extra field accepted from the public order.
    /// </summary>
    public static BookingWriteSet CreateMerchant(Guid merchantId, string externalOrderId, string acceptedPriceVersion,
        ParcelDetails details, PricingTier tier, PriceBreakdown price, Guid actorId,
        ITrackingNumberGenerator trackingNumbers, DateTimeOffset createdAt, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(tier);
        if (!string.Equals(acceptedPriceVersion, tier.PricingVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("The accepted pricing version must match the validated tariff.", nameof(acceptedPriceVersion));
        }

        var parcel = Parcel.ForMerchant(Guid.NewGuid(), merchantId, externalOrderId,
            trackingNumbers.Generate(), details, createdAt);
        return Create(parcel, tier, price, actorId, correlationId);
    }

    public static BookingWriteSet Create(
        Parcel parcel, PricingTier tier, PriceBreakdown price, Guid actorId, string correlationId)
    {
        if (parcel.Status != ParcelStatus.Created || parcel.Version != 1
            || parcel.ZoneId != tier.ZoneId || parcel.Size != tier.Size
            || !PriceCalculator.TryCalculate(tier.BaseRate, tier.PerKgRate, parcel.WeightKg, out var expected)
            || expected != price)
        {
            throw new ArgumentException("Booking details and retained pricing must match.");
        }

        var snapshot = new PriceSnapshot(Guid.NewGuid(), parcel.Id, tier,
            price.BaseFee, price.WeightFee, price.Total, parcel.CreatedAt);
        return new(parcel, snapshot, BookingHistory.Created(Guid.NewGuid(), parcel, actorId),
            OutboxMessage.ParcelCreated(Guid.NewGuid(), parcel, correlationId));
    }
}
