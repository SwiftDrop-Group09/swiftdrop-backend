using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SwiftDrop.Delivery.Api.Application.Bookings;

public sealed record NormalizedConfirmation(Guid QuoteId, decimal AcceptTotal, string IdempotencyKey, string Fingerprint);
public sealed record BookingInputValidationResult(
    NormalizedConfirmation? Input, IReadOnlyDictionary<string, string[]> Errors);

public static class BookingInputValidator
{
    public static BookingInputValidationResult Validate(ConfirmInput? input, string? idempotencyKey)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128
            || idempotencyKey.Any(char.IsControl))
        {
            errors["Idempotency-Key"] = ["Supply one nonblank key of at most 128 characters without control characters."];
        }

        if (input is null)
        {
            errors["body"] = ["A booking confirmation is required."];
            return new(null, errors);
        }

        if (!Guid.TryParse(input.QuoteId?.Trim(), out var quoteId) || quoteId == Guid.Empty)
        {
            errors["quoteId"] = ["A valid nonempty quote identifier is required."];
        }

        if (input.AcceptTotal is not { } total || total is < 0 or >= 10000000000000000m
            || decimal.Round(total, 2, MidpointRounding.AwayFromZero) != total)
        {
            errors["acceptTotal"] = ["Supply a nonnegative total that fits the supported monetary range and has at most two decimal places."];
        }

        if (errors.Count > 0)
        {
            return new(null, errors);
        }

        var accepted = input.AcceptTotal!.Value;
        var canonical = "{\"quoteId\":\"" + quoteId.ToString("D")
            + "\",\"acceptTotal\":" + accepted.ToString("G29", CultureInfo.InvariantCulture) + "}";
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(new(quoteId, accepted, idempotencyKey!, fingerprint), errors);
    }
}
