using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Application.Serialization;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Bookings;

internal static class BookingQuoteMaterializer
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new ExactDecimalConverter() }
    };

    public static bool TryMaterialize(BookingQuote bookingQuote, out ParcelDetails? details, out PriceBreakdown price)
    {
        details = null;
        price = default;
        var quote = bookingQuote.Quote;
        var tier = bookingQuote.Tier;
        QuoteInput? input;
        try
        {
            input = JsonSerializer.Deserialize<QuoteInput>(quote.DeliveryInput, InputJson);
        }
        catch (JsonException)
        {
            return false;
        }

        var validation = QuoteInputValidator.Validate(input);
        if (validation.Input is not { } normalized || tier.Id != quote.PricingTierId
            || tier.PricingVersion != quote.PricingVersion || tier.Currency != quote.Currency
            || quote.Currency != "LKR" || tier.ZoneId != normalized.DeliveryAddress.ZoneId
            || tier.Size != normalized.PackageDetails.Size)
        {
            return false;
        }

        var canonicalInput = QuoteCanonicalInput.Serialize(normalized);
        if (!string.Equals(quote.RequestFingerprint,
            QuoteCanonicalInput.Fingerprint(quote.OwnerId, canonicalInput, tier), StringComparison.Ordinal)
            || !PriceCalculator.TryCalculate(tier.BaseRate, tier.PerKgRate, normalized.PackageDetails.WeightKg, out price)
            || price.Total != quote.Total)
        {
            return false;
        }

        // Retained tariffs can be inactive: a still-valid quote keeps its original price.
        details = new(normalized.PackageDetails.Size, normalized.PackageDetails.WeightKg,
            normalized.DeliveryAddress.ZoneId,
            JsonSerializer.Serialize(normalized.PickupAddress, InputJson), null,
            JsonSerializer.Serialize(normalized.DeliveryAddress, InputJson),
            JsonSerializer.Serialize(normalized.Recipient, InputJson));
        return true;
    }
}
