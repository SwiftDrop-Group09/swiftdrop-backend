using System.Globalization;

namespace SwiftDrop.Delivery.Api.Application.DeliveryReads;

internal static class DeliveryPagination
{
    public static DeliveryPageQuery Parse(string? pageInput, string? sizeInput, Dictionary<string, string[]> errors)
    {
        var page = ParseValue(pageInput, "page", 1, int.MaxValue, errors);
        var size = ParseValue(sizeInput, "pageSize", 20, 100, errors);
        if ((long)(page - 1) * size > int.MaxValue) errors["page"] = ["The requested page offset is too large."];
        return new(page, size);
    }

    private static int ParseValue(string? input, string field, int defaultValue, int maximum, Dictionary<string, string[]> errors)
    {
        if (input is null) return defaultValue;
        if (int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 1 && value <= maximum)
            return value;
        errors[field] = ["Supply a positive integer within the documented range."];
        return defaultValue;
    }
}
