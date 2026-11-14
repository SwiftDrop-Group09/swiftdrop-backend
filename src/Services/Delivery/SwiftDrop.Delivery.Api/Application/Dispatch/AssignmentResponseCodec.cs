using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Dispatch;

internal static class AssignmentResponseCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static AssignedResponse Create(AssignmentState state) => new(200,
        JsonSerializer.Serialize(new ParcelBookingResponse(state.Parcel.Id, state.Parcel.TrackingNumber,
            state.Parcel.Status.ToString(), state.Parcel.Version,
            new(state.Snapshot.Total, state.Snapshot.Currency, state.Snapshot.PricingVersion), state.Parcel.CreatedAt), Json),
        Location(state.Parcel.Id));

    public static AssignedResponse Replay(IdempotencyRecord record, AssignCourierCommand command)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<AssignedResponse>(record.StoredResponse ?? "null", Json);
            if (stored is not null)
            {
                using var document = JsonDocument.Parse(stored.Body);
                var fields = document.RootElement.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal);
                if (!fields.SequenceEqual(new[] { "createdAt", "id", "priceSnapshot", "status", "trackingNumber", "version" }))
                    throw new AssignmentException(AssignmentFailure.Unavailable);
            }
            var parcel = stored is null ? null : JsonSerializer.Deserialize<ParcelBookingResponse>(stored.Body, Json);
            if (record.ParcelId != command.ParcelId || stored is not { StatusCode: 200 } || stored.Location != Location(command.ParcelId)
                || parcel is null || parcel.Id != command.ParcelId || parcel.Status != "Assigned"
                || parcel.Version != checked(command.ExpectedVersion + 1) || string.IsNullOrWhiteSpace(parcel.TrackingNumber)
                || parcel.PriceSnapshot is not { Currency: "LKR", Total: >= 0 } price || string.IsNullOrWhiteSpace(price.PricingVersion)
                || parcel.CreatedAt.Offset != TimeSpan.Zero) throw new AssignmentException(AssignmentFailure.Unavailable);
            return stored;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { throw new AssignmentException(AssignmentFailure.Unavailable, inner: exception); }
    }
    private static string Location(Guid parcelId) => "/api/v1/parcels/" + parcelId.ToString("D");
}
