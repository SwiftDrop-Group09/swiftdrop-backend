using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SwiftDrop.Delivery.Api.Application.Serialization;

namespace SwiftDrop.Delivery.Tests.Quotes;

public sealed class ExactDecimalTests
{
    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("0.0000000000000000000000000000000000", "0")]
    [InlineData("-0e10", "0")]
    [InlineData("2.5000", "2.5")]
    [InlineData("25e-1", "2.5")]
    [InlineData("2500E-3", "2.5")]
    [InlineData("2.5e+0000000000", "2.5")]
    [InlineData("1.0050000000000000000000000000000000000", "1.005")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("0.00000000000000000000000000010", "0.0000000000000000000000000001")]
    [InlineData("1e-28", "0.0000000000000000000000000001")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335", "-79228162514264337593543950335")]
    public void ExactlyRepresentableNumbersAcceptEquivalentExponentsAndTrailingZeros(string json, string expected)
    {
        var actual = JsonSerializer.Deserialize<decimal>(json, Options());

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), actual);
    }

    [Theory]
    [InlineData("30.00000000000000000000000000001")]
    [InlineData("1.004999999999999999999999999999999")]
    [InlineData("1.0000000000000000000000000000000001")]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("-0.00000000000000000000000000001")]
    [InlineData("1e-29")]
    [InlineData("1e-1000000000000000000000000000")]
    [InlineData("1e1000000000000000000000000000")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("\"2.5\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void RoundedUnderflowedOverflowedOrNonnumericInputsAreRejected(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<decimal>(json, Options()));
    }

    [Fact]
    public void WriterPreservesTheDecimalValue()
    {
        var options = Options();
        var json = JsonSerializer.Serialize(2.500m, options);

        Assert.Equal(2.5m, JsonSerializer.Deserialize<decimal>(json, options));
        Assert.DoesNotContain('"', json);
    }

    [Fact]
    public void SegmentedNumbersReceiveTheSameExactnessValidation()
    {
        Assert.Equal(2.5m, ReadSegmented("25", "e-1"));
        Assert.Throws<JsonException>(() => ReadSegmented("30.0000000000000000000", "0000000001"));
    }

    private static decimal ReadSegmented(string firstPart, string secondPart)
    {
        var first = new NumberSegment(Encoding.UTF8.GetBytes(firstPart));
        var last = first.Append(Encoding.UTF8.GetBytes(secondPart));
        var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        var reader = new Utf8JsonReader(sequence);
        Assert.True(reader.Read());
        Assert.True(reader.HasValueSequence);
        return new ExactDecimalConverter().Read(ref reader, typeof(decimal), Options());
    }

    private sealed class NumberSegment : ReadOnlySequenceSegment<byte>
    {
        public NumberSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public NumberSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new NumberSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private static JsonSerializerOptions Options() => new() { Converters = { new ExactDecimalConverter() } };
}
