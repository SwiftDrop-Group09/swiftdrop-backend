using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwiftDrop.Delivery.Api.Application.Idempotency;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Dispatch;

public static class AssignmentValidator
{
    public const string Operation = "AssignCourier";
    public const string UnassignOperation = "UnassignCourier";
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
        string? reason = null;
        if (input.Reassign)
        {
            try { reason = ReassignmentReason.Normalize(input.Reason); }
            catch (ArgumentException) { errors["reason"] = ["Supply a reassignment reason of 1 to 500 characters."]; }
        }
        else if (input.Reason is not null) errors["reason"] = ["A reason only belongs to reassignment."];
        var date = Date(input.ServiceDate, errors);
        if (errors.Count != 0) return null;
        // Keep omission in the fingerprint: replay must not change at business midnight.
        var canonical = input.Reassign
            ? JsonSerializer.Serialize(new { parcelId = parcel, courierId = courier,
                expectedVersion = input.ExpectedVersion!.Value, reassign = true, serviceDate = input.ServiceDate, reason })
            : JsonSerializer.Serialize(new { parcelId = parcel, courierId = courier,
            expectedVersion = input.ExpectedVersion!.Value, reassign = false, serviceDate = input.ServiceDate });
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(parcel, courier, input.ExpectedVersion.Value, date, key!, fingerprint, input.Reassign, reason);
    }

    public static UnassignCourierCommand? ValidateUnassign(UnassignCourierInput? input, string? key, Dictionary<string, string[]> errors)
    {
        if (!IdempotencyInput.IsValidKey(key)) errors["Idempotency-Key"] = ["Supply one nonblank key of at most 128 characters without control characters."];
        if (input is null) { errors["body"] = ["Supply an unassignment request."]; return null; }
        if (!Guid.TryParse(input.ParcelId, out var parcel) || parcel == Guid.Empty) errors["parcelId"] = ["Supply a non-empty parcel UUID."];
        if (input.ExpectedVersion is not >= 1) errors["expectedVersion"] = ["Supply an expected version of at least 1."];
        string? reason = null;
        if (input.Reason is not null)
        {
            try { reason = ReassignmentReason.Normalize(input.Reason); }
            catch (ArgumentException) { errors["reason"] = ["Supply an unassignment reason of 1 to 500 characters."]; }
        }
        if (errors.Count != 0) return null;
        var canonical = JsonSerializer.Serialize(new { parcelId = parcel, expectedVersion = input.ExpectedVersion!.Value, reason });
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(parcel, input.ExpectedVersion.Value, key!, fingerprint, reason);
    }
}
