using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Quotes;

public sealed class QuoteEstimator(ICurrentCustomer currentCustomer, IQuoteStore store, TimeProvider timeProvider)
{
    public async Task<QuoteEstimationResult> ExecuteAsync(QuoteInput? input, CancellationToken cancellationToken = default)
    {
        if (!currentCustomer.IsAuthenticated || currentCustomer.CustomerId is not { } ownerId || ownerId == Guid.Empty)
        {
            return new(null, QuoteFailure.Unauthenticated);
        }

        if (!currentCustomer.IsCustomer)
        {
            return new(null, QuoteFailure.Forbidden);
        }

        var validation = QuoteInputValidator.Validate(input);
        if (validation.Input is not { } normalized)
        {
            return new(null, QuoteFailure.InvalidInput, validation.Errors);
        }

        try
        {
            var pricing = await store.ReadPricingAsync(normalized.PickupAddress.ZoneId,
                normalized.DeliveryAddress.ZoneId, normalized.PackageDetails.Size, cancellationToken);
            if (!pricing.PickupZoneActive || !pricing.DeliveryZoneActive)
            {
                return new(null, QuoteFailure.UnsupportedZone);
            }

            if (pricing.ActiveTiers.Count != 1)
            {
                return new(null, QuoteFailure.PricingUnavailable);
            }

            var tier = pricing.ActiveTiers[0];
            if (!tier.IsActive || tier.ZoneId != normalized.DeliveryAddress.ZoneId
                || tier.Size != normalized.PackageDetails.Size || tier.Currency != "LKR"
                || !PriceCalculator.TryCalculate(tier.BaseRate, tier.PerKgRate, normalized.PackageDetails.WeightKg, out var price))
            {
                return new(null, QuoteFailure.PricingUnavailable);
            }

            var canonicalInput = QuoteCanonicalInput.Serialize(normalized);
            var fingerprint = QuoteCanonicalInput.Fingerprint(ownerId, canonicalInput, tier);
            var quote = new Quote(Guid.NewGuid(), ownerId, fingerprint, canonicalInput,
                tier, price.Total, timeProvider.GetUtcNow());
            await store.SaveAsync(quote, cancellationToken);

            return new(new QuoteResponse(quote.Id, quote.Total, quote.Currency,
                quote.PricingVersion, quote.ExpiresAt, quote.RequestFingerprint), null);
        }
        catch (QuoteStoreUnavailableException)
        {
            return new(null, QuoteFailure.PersistenceUnavailable);
        }
    }
}
