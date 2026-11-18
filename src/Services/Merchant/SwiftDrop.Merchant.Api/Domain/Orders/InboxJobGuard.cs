using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwiftDrop.Merchant.Api.Domain.Orders;

/// <summary>Input guards specific to merchant orders. General guards live in <see cref="DomainGuard"/>.</summary>
internal static partial class InboxJobGuard
{
    /// <summary>
    /// Caller-supplied identifiers (external order id, idempotency key) are
    /// compared byte-for-byte, so they are rejected rather than silently trimmed.
    /// </summary>
    public static string Identifier(string value, string name, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(name, $"'{name}' cannot exceed {maxLength} characters.");
        }

        if (value.Trim().Length != value.Length || value.Any(char.IsControl))
        {
            throw new ArgumentException($"'{name}' cannot contain leading/trailing whitespace or control characters.", name);
        }

        return value;
    }

    public static string Fingerprint(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        return LowercaseSha256Hex().IsMatch(value)
            ? value
            : throw new ArgumentException($"'{name}' must be a lowercase SHA-256 hex string.", name);
    }

    /// <summary>Error codes are stored and shown to merchants, so only short snake_case codes are allowed — never raw exception text.</summary>
    public static string ErrorCode(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return SafeErrorCode().IsMatch(value)
            ? value
            : throw new ArgumentException($"'{name}' must be a snake_case code of at most {InboxJobLimits.ErrorCodeMaxLength} characters.", name);
    }

    public static string Currency(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        return IsoCurrency().IsMatch(value)
            ? value
            : throw new ArgumentException($"'{name}' must be a three-letter upper-case ISO 4217 code.", name);
    }

    public static string ParcelStatus(string value, string name) => ParcelStatuses.IsKnown(value)
        ? value
        : throw new ArgumentException($"'{name}' is not a known parcel status.", name);

    public static string JsonObject(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > InboxJobLimits.OrderPayloadMaxLength)
        {
            throw new ArgumentOutOfRangeException(name, $"'{name}' cannot exceed {InboxJobLimits.OrderPayloadMaxLength} characters.");
        }

        try
        {
            using var document = JsonDocument.Parse(value, OrderFingerprint.StrictParsing);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? value
                : throw new ArgumentException($"'{name}' must be a JSON object.", name);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"'{name}' must be valid JSON.", name, exception);
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex LowercaseSha256Hex();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex SafeErrorCode();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex IsoCurrency();
}
