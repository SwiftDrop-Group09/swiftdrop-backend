using System.Text.Json;

namespace SwiftDrop.Delivery.Api.Application.Admin.Zones;

public enum ZoneFailure
{
    Unauthenticated,
    Forbidden,
    NotFound,
    Conflict,
    InvalidInput,
    OverlappingPostalCodes
}

public sealed record ZoneInput(
    string Name,
    string? City,
    string[]? ServicePostalCodes,
    bool IsActive = true,
    long? ExpectedVersion = null);

public sealed record ZoneDto(
    Guid Id,
    string Name,
    string? City,
    IReadOnlyList<string> ServicePostalCodes,
    bool IsActive,
    long Version);

public sealed record ZoneSummaryItemDto(
    Guid ZoneId,
    string Name,
    IReadOnlyDictionary<string, int> StatusCounts,
    int Total);

public sealed record ZoneSummaryResponse(IReadOnlyList<ZoneSummaryItemDto> Zones);

public sealed record ZonePage(
    IReadOnlyList<ZoneDto> Items,
    long TotalCount,
    int Page,
    int PageSize);

public sealed record ZoneResult<T>(T? Response, ZoneFailure? Failure = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public static class ZoneBoundaryHelper
{
    public static string SerializeBoundaries(string? city, string[]? postalCodes)
    {
        var cleanCity = string.IsNullOrWhiteSpace(city) ? null : city.Trim();
        var cleanCodes = postalCodes?
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        return JsonSerializer.Serialize(new
        {
            city = cleanCity,
            servicePostalCodes = cleanCodes
        });
    }

    public static (string? City, string[] ServicePostalCodes) DeserializeBoundaries(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, []);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? city = root.TryGetProperty("city", out var cityEl) && cityEl.ValueKind == JsonValueKind.String
                ? cityEl.GetString() : null;

            var codes = new List<string>();
            if (root.TryGetProperty("servicePostalCodes", out var codesEl) && codesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in codesEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                    {
                        codes.Add(s.Trim());
                    }
                }
            }

            return (city, codes.ToArray());
        }
        catch
        {
            return (null, []);
        }
    }
}
