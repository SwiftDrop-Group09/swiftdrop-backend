namespace SwiftDrop.Merchant.Api.Domain.Orders;

/// <summary>
/// What Delivery returns once it has created (or found) the parcel for an order.
/// Mirrors the <c>BookingResult</c> message in <c>delivery.proto</c>, already parsed
/// into domain types so the entity never deals with wire formats.
/// </summary>
public sealed record BookingConfirmation
{
    public BookingConfirmation(
        Guid parcelId,
        string trackingNumber,
        ParcelStatusSnapshot parcelStatus,
        decimal total,
        string currency,
        string pricingVersion)
    {
        ArgumentNullException.ThrowIfNull(parcelStatus);
        ParcelId = DomainGuard.Id(parcelId, nameof(parcelId));
        TrackingNumber = DomainGuard.Text(trackingNumber, nameof(trackingNumber), InboxJobLimits.TrackingNumberMaxLength);
        ParcelStatus = parcelStatus;
        Total = total >= 0 ? total : throw new ArgumentOutOfRangeException(nameof(total), "Total cannot be negative.");
        Currency = InboxJobGuard.Currency(currency, nameof(currency));
        PricingVersion = DomainGuard.Text(pricingVersion, nameof(pricingVersion), InboxJobLimits.PricingVersionMaxLength);
    }

    public Guid ParcelId { get; }
    public string TrackingNumber { get; }
    public ParcelStatusSnapshot ParcelStatus { get; }
    public decimal Total { get; }
    public string Currency { get; }
    public string PricingVersion { get; }
}
