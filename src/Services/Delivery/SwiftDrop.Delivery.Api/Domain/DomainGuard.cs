using System.Text.Json;

namespace SwiftDrop.Delivery.Api.Domain;

internal static class DomainGuard
{
    public static Guid Id(Guid value, string name) => value != Guid.Empty
        ? value : throw new ArgumentException("An identifier is required.", name);

    public static string Text(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }

    public static decimal Money(decimal value, string name) =>
        value >= 0 && value < 10000000000000000m && decimal.Round(value, 2, MidpointRounding.AwayFromZero) == value
            ? value : throw new ArgumentOutOfRangeException(name, "Money must be nonnegative, fit numeric(18,2), and have at most two decimal places.");

    public static string Currency(string value) => value == "LKR"
        ? value : throw new ArgumentException("Only LKR is supported.", nameof(value));

    public static string JsonObject(string value, string name)
    {
        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A JSON object is required.", name);
        }

        return value;
    }
}
