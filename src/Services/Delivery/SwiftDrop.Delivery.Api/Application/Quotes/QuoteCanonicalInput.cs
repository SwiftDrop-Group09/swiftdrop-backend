using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Quotes;

public static class QuoteCanonicalInput
{
    public static string Serialize(NormalizedQuoteInput input)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteAddress(writer, "pickupAddress", input.PickupAddress);
            WriteAddress(writer, "deliveryAddress", input.DeliveryAddress);
            writer.WriteStartObject("packageDetails");
            writer.WritePropertyName("weightKg");
            writer.WriteRawValue(input.PackageDetails.WeightKg.ToString("G29", CultureInfo.InvariantCulture));
            writer.WriteString("size", input.PackageDetails.Size.ToString());
            writer.WriteEndObject();
            writer.WriteStartObject("recipient");
            writer.WriteString("name", input.Recipient.Name);
            writer.WriteString("phoneNumber", input.Recipient.PhoneNumber);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Fingerprint(Guid ownerId, string canonicalInput, PricingTier tier)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("ownerId", ownerId.ToString("D"));
            writer.WritePropertyName("deliveryInput");
            writer.WriteRawValue(canonicalInput);
            writer.WriteString("pricingTierId", tier.Id.ToString("D"));
            writer.WriteString("pricingVersion", tier.PricingVersion);
            writer.WriteString("currency", tier.Currency);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteAddress(Utf8JsonWriter writer, string field, NormalizedAddress address)
    {
        writer.WriteStartObject(field);
        writer.WriteString("street", address.Street);
        writer.WriteString("city", address.City);
        writer.WriteString("postalCode", address.PostalCode);
        writer.WriteString("zoneId", address.ZoneId.ToString("D"));
        writer.WriteEndObject();
    }
}
