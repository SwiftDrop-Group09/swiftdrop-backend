using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Quotes;

public sealed record QuotePricingData(
    bool PickupZoneActive,
    bool DeliveryZoneActive,
    IReadOnlyList<PricingTier> ActiveTiers);

public interface IQuoteStore
{
    Task<QuotePricingData> ReadPricingAsync(
        Guid pickupZoneId, Guid deliveryZoneId, ParcelSize size, CancellationToken cancellationToken);

    Task SaveAsync(Quote quote, CancellationToken cancellationToken);
}

public sealed class QuoteStoreUnavailableException : Exception
{
    public QuoteStoreUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
