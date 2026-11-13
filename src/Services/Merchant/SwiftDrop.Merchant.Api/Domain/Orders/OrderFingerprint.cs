using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwiftDrop.Merchant.Api.Domain.Orders;

/// <summary>
/// Canonical request fingerprint for merchant orders: lowercase hex SHA-256 of
/// the payload re-serialised with object keys sorted (ordinal), no whitespace,
/// array order preserved, and scalar values written exactly as received
/// (so <c>"2.50"</c> and <c>"2.5"</c> differ).
/// </summary>
/// <remarks>
/// PROVISIONAL: the cross-service recipe is still open with the Delivery owner
/// (docs/delivery-merchant-booking-handoff.md, "Decisions and remaining owner
/// acceptance"). Keep every fingerprint computation behind this class so the
/// recipe can change in one place. Do not reuse the Customer quote fingerprint.
/// </remarks>
public static class OrderFingerprint
{
    /// <summary>Duplicate keys would make "the" value ambiguous, so they are rejected outright.</summary>
    internal static JsonDocumentOptions StrictParsing { get; } = new() { AllowDuplicateProperties = false };

    public static string Compute(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonNode root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: StrictParsing)
                ?? throw new ArgumentException("Order payload cannot be JSON null.", nameof(json));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Order payload must be valid JSON.", nameof(json), exception);
        }

        return Compute(root);
    }

    public static string Compute(JsonNode payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, payload);
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;

            case JsonObject jsonObject:
                writer.WriteStartObject();
                foreach (var (key, value) in jsonObject.OrderBy(property => property.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteCanonical(writer, value);
                }
                writer.WriteEndObject();
                break;

            case JsonArray jsonArray:
                writer.WriteStartArray();
                foreach (var item in jsonArray)
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;

            default:
                node.WriteTo(writer);
                break;
        }
    }
}
