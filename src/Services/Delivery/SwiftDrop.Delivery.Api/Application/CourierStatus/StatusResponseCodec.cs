using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.CourierStatus;

internal static class StatusResponseCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static StoredStatusResponse Create(Parcel parcel, PriceSnapshot snapshot) => new(200,
        JsonSerializer.Serialize(new ParcelBookingResponse(parcel.Id, parcel.TrackingNumber, parcel.Status.ToString(), parcel.Version,
            new(snapshot.Total, snapshot.Currency, snapshot.PricingVersion), parcel.CreatedAt), Json), Location(parcel.Id));

    public static bool TryRead(IdempotencyRecord record, CourierStatusCommand command, out StoredStatusResponse? response)
    {
        response = null;
        if (record.ParcelId != command.ParcelId || record.StoredResponse is null) return false;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredStatusResponse>(record.StoredResponse, Json);
            if (stored is null || stored.StatusCode != 200 || stored.Location != Location(command.ParcelId)
                || string.IsNullOrWhiteSpace(stored.Body)) return false;
            using var document = JsonDocument.Parse(stored.Body);
            var fields = document.RootElement.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal);
            if (!fields.SequenceEqual(new[] { "createdAt", "id", "priceSnapshot", "status", "trackingNumber", "version" })) return false;
            var parcel = JsonSerializer.Deserialize<ParcelBookingResponse>(stored.Body, Json);
            if (parcel is null || parcel.Id != command.ParcelId || parcel.Status != command.Status.ToString()
                || parcel.Version != checked(command.ExpectedVersion + 1) || string.IsNullOrWhiteSpace(parcel.TrackingNumber)
                || parcel.PriceSnapshot is not { Currency: "LKR", Total: >= 0 } price || string.IsNullOrWhiteSpace(price.PricingVersion)
                || parcel.CreatedAt.Offset != TimeSpan.Zero) return false;
            response = stored;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static string Location(Guid id) => "/api/v1/parcels/" + id.ToString("D");
}
