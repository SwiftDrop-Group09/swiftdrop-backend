using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwiftDrop.Delivery.Api.Application.Serialization;

/// <summary>Rejects JSON numbers that decimal parsing would silently round or underflow.</summary>
public sealed class ExactDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDecimal(out var value))
        {
            throw InvalidNumber();
        }

        var raw = reader.HasValueSequence
            ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
            : Encoding.UTF8.GetString(reader.ValueSpan);
        var parsed = value.ToString("G29", CultureInfo.InvariantCulture);
        if (Normalize(raw) != Normalize(parsed))
        {
            throw InvalidNumber();
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);

    private static NormalizedNumber Normalize(string number)
    {
        var negative = number[0] == '-';
        var start = negative ? 1 : 0;
        var exponentIndex = number.IndexOfAny(['e', 'E']);
        var mantissaEnd = exponentIndex < 0 ? number.Length : exponentIndex;
        var pointIndex = number.IndexOf('.', start, mantissaEnd - start);
        var fractionalDigits = pointIndex < 0 ? 0 : mantissaEnd - pointIndex - 1;
        var digits = number[start..mantissaEnd].Replace(".", "", StringComparison.Ordinal);
        var firstDigit = 0;
        while (firstDigit < digits.Length && digits[firstDigit] == '0')
        {
            firstDigit++;
        }

        // All textual forms of zero are equal regardless of their sign or exponent.
        if (firstDigit == digits.Length)
        {
            return new(false, "0", 0);
        }

        var lastDigit = digits.Length - 1;
        while (digits[lastDigit] == '0')
        {
            lastDigit--;
        }

        var exponent = 0L;
        if (exponentIndex >= 0 && !long.TryParse(number.AsSpan(exponentIndex + 1),
            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
        {
            throw InvalidNumber();
        }

        try
        {
            exponent = checked(exponent - fractionalDigits + (digits.Length - lastDigit - 1));
        }
        catch (OverflowException)
        {
            throw InvalidNumber();
        }

        return new(negative, digits[firstDigit..(lastDigit + 1)], exponent);
    }

    private static JsonException InvalidNumber() =>
        new("The number must be exactly representable as a decimal.");

    private readonly record struct NormalizedNumber(bool Negative, string Digits, long Exponent);
}
