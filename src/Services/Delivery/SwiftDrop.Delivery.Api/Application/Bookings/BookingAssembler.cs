using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Bookings;

/// <summary>Builds persistence records after an entry adapter has authorized parcel creation.</summary>
public static class BookingAssembler
{
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
