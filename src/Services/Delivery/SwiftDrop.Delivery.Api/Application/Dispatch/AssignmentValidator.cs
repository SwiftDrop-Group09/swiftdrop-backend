using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwiftDrop.Delivery.Api.Application.Idempotency;

namespace SwiftDrop.Delivery.Api.Application.Dispatch;

public static class AssignmentValidator
{
    public const string Operation = "AssignCourier";
    public static DateOnly? Date(string? text, IDictionary<string, string[]> errors)
    {
        if (text is null) return null;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        errors["serviceDate"] = ["Supply a calendar date in YYYY-MM-DD format."];
        return null;
    }

    public static AssignCourierCommand? Validate(AssignCourierInput? input, string? key, Dictionary<string, string[]> errors)
    {
        if (!IdempotencyInput.IsValidKey(key)) errors["Idempotency-Key"] = ["Supply one nonblank key of at most 128 characters without control characters."];
        if (input is null) { errors["body"] = ["Supply an assignment request."]; return null; }
        if (!Guid.TryParse(input.ParcelId, out var parcel) || parcel == Guid.Empty) errors["parcelId"] = ["Supply a non-empty parcel UUID."];
        if (!Guid.TryParse(input.CourierId, out var courier) || courier == Guid.Empty) errors["courierId"] = ["Supply a non-empty courier UUID."];
        if (input.ExpectedVersion is not >= 1) errors["expectedVersion"] = ["Supply an expected version of at least 1."];
        if (input.Reassign) errors["reassign"] = ["Reassignment is not supported by this milestone."];
        var date = Date(input.ServiceDate, errors);
        if (errors.Count != 0) return null;
        // Keep omission in the fingerprint: replay must not change at business midnight.
        var canonical = JsonSerializer.Serialize(new { parcelId = parcel, courierId = courier,
            expectedVersion = input.ExpectedVersion!.Value, reassign = false, serviceDate = input.ServiceDate });
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(parcel, courier, input.ExpectedVersion.Value, date, key!, fingerprint);
    }
}
