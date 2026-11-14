using System.Text.RegularExpressions;

namespace SwiftDrop.Merchant.Api.Infrastructure.Authentication.ApiKeys;

/// <summary>
/// The lookup part of a presented key. Current keys embed their full id
/// (<c>swd_test_&lt;32 hex&gt;_&lt;secret&gt;</c>) and are found by primary key;
/// legacy keys only carry an 8-hex prefix and are found by prefix.
/// </summary>
public readonly record struct ParsedApiKey(string Prefix, Guid? KeyId)
{
    public bool IsLegacy => KeyId is null;
}

/// <summary>
/// Validates the shape of a presented key before any database access, so
/// malformed or oversized input is rejected cheaply.
/// </summary>
public static partial class ApiKeyParser
{
    private const int MaxKeyLength = 256;

    public static bool TryParse(string? presented, out ParsedApiKey parsed)
    {
        parsed = default;
        if (presented is null || presented.Length > MaxKeyLength)
        {
            return false;
        }

        var match = KeyShape().Match(presented);
        if (!match.Success)
        {
            return false;
        }

        var id = match.Groups["id"].Value;
        parsed = new ParsedApiKey(
            match.Groups["prefix"].Value,
            id.Length == 32 ? Guid.ParseExact(id, "N") : null);
        return true;
    }

    [GeneratedRegex("^(?<prefix>swd_test_(?<id>[0-9a-f]{32}|[0-9a-f]{8}))_[A-Za-z0-9]{16,128}$")]
    private static partial Regex KeyShape();
}
