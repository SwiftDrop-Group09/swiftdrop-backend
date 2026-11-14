using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwiftDrop.Delivery.Api.Application.Idempotency;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.CourierStatus;

public static class CourierStatusValidator
{
    public const string Operation = "UpdateDeliveryStatus";

    public static CourierStatusValidation Validate(Guid parcelId, CourierStatusInput? input, string? key)
    {
        var errors = new Dictionary<string, string[]>();
        if (!IdempotencyInput.IsValidKey(key))
            errors["Idempotency-Key"] = ["Supply one nonblank key of at most 128 characters without control characters."];
        if (input is null)
        {
            errors["body"] = ["A status request is required."];
            return new(null, errors);
        }

        var statusText = input.Status?.Trim();
        if (!Enum.TryParse<ParcelStatus>(statusText, out var status) || !Enum.IsDefined(status) || statusText != status.ToString())
            errors["status"] = ["Use a canonical parcel status."];
        if (input.ExpectedVersion is not >= 1) errors["expectedVersion"] = ["Supply an expected parcel version of at least 1."];
        var notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        if (input.Notes is { Length: > 500 }) errors["notes"] = ["Notes must not exceed 500 characters."];
        if (status is ParcelStatus.PickedUp or ParcelStatus.InTransit or ParcelStatus.OutForDelivery && notes is null)
            errors["notes"] = ["A nonblank private delivery note is required for this stage."];
        CourierFailureReason? reason = null;
        if (status == ParcelStatus.Failed)
        {
            var text = input.FailureReason?.Trim();
            if (Enum.TryParse<CourierFailureReason>(text, out var parsed) && Enum.IsDefined(parsed) && text == parsed.ToString()) reason = parsed;
            else errors["failureReason"] = ["Supply RecipientUnavailable, AddressUnreachable, Damaged or Other."];
        }
        else if (input.FailureReason is not null)
            errors["failureReason"] = ["A failure reason only belongs to Failed."];
        if (errors.Count != 0) return new(null, errors);

        var canonical = JsonSerializer.Serialize(new
        {
            parcelId = parcelId.ToString("D"), status = status.ToString(), expectedVersion = input.ExpectedVersion!.Value,
            notes, failureReason = reason?.ToString()
        });
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(new(parcelId, status, input.ExpectedVersion.Value, notes, reason, key!, fingerprint), errors);
    }
}
