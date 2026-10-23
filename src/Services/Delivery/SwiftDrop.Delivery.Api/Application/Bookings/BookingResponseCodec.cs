using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Bookings;

internal static class BookingResponseCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static StoredBookingResponse Create(BookingWriteSet write)
    {
        var parcel = write.Parcel;
        var snapshot = write.Snapshot;
        var response = new ParcelBookingResponse(parcel.Id, parcel.TrackingNumber, parcel.Status.ToString(), parcel.Version,
            new(snapshot.Total, snapshot.Currency, snapshot.PricingVersion), parcel.CreatedAt);
        return new(201, JsonSerializer.Serialize(response, Json), Location(parcel.Id));
    }

    public static bool TryRead(IdempotencyRecord record, decimal acceptedTotal, out StoredBookingResponse? response)
    {
        response = null;
        if (record.ParcelId is not { } parcelId || record.StoredResponse is null)
        {
            return false;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredBookingResponse>(record.StoredResponse, Json);
            if (stored is null || stored.StatusCode != 201 || stored.Location != Location(parcelId)
                || string.IsNullOrWhiteSpace(stored.Body))
            {
                return false;
            }

            using var document = JsonDocument.Parse(stored.Body);
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Count() != 6
                || !body.TryGetProperty("id", out var id) || id.GetGuid() != parcelId
                || !body.TryGetProperty("trackingNumber", out var tracking) || string.IsNullOrWhiteSpace(tracking.GetString())
                || !body.TryGetProperty("status", out var status) || status.GetString() != "Created"
                || !body.TryGetProperty("version", out var version) || version.GetInt64() != 1
                || !body.TryGetProperty("createdAt", out var created) || !created.TryGetDateTimeOffset(out var createdAt)
                || createdAt.Offset != TimeSpan.Zero
                || !body.TryGetProperty("priceSnapshot", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object
                || snapshot.EnumerateObject().Count() != 3
                || !snapshot.TryGetProperty("total", out var total) || total.GetDecimal() != acceptedTotal
                || !snapshot.TryGetProperty("currency", out var currency) || currency.GetString() != "LKR"
                || !snapshot.TryGetProperty("pricingVersion", out var pricingVersion) || string.IsNullOrWhiteSpace(pricingVersion.GetString()))
            {
                return false;
            }

            response = stored;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static string Location(Guid parcelId) => "/api/v1/parcels/" + parcelId.ToString("D");
}
