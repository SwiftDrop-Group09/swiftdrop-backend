using SwiftDrop.Merchant.Api.Domain.Orders;

namespace SwiftDrop.Merchant.Tests.Orders;

public sealed class OrderFingerprintTests
{
    [Fact]
    public void Compute_IsIndependentOfKeyOrderAtEveryDepth()
    {
        var a = OrderFingerprint.Compute("""{"b":1,"a":{"y":"2","x":"1"}}""");
        var b = OrderFingerprint.Compute("""{"a":{"x":"1","y":"2"},"b":1}""");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_IgnoresWhitespace()
    {
        var compact = OrderFingerprint.Compute("""{"a":[1,2],"b":"x"}""");
        var spaced = OrderFingerprint.Compute("""
            {
              "a": [ 1, 2 ],
              "b": "x"
            }
            """);

        Assert.Equal(compact, spaced);
    }

    [Fact]
    public void Compute_PreservesArrayOrder() =>
        Assert.NotEqual(
            OrderFingerprint.Compute("""{"a":[1,2]}"""),
            OrderFingerprint.Compute("""{"a":[2,1]}"""));

    [Fact]
    public void Compute_KeepsExactDecimalStrings() =>
        Assert.NotEqual(
            OrderFingerprint.Compute("""{"weightKg":"2.5"}"""),
            OrderFingerprint.Compute("""{"weightKg":"2.50"}"""));

    [Fact]
    public void Compute_ChangesWhenAnyValueChanges() =>
        Assert.NotEqual(
            OrderFingerprint.Compute("""{"recipient":{"name":"Fictional A"}}"""),
            OrderFingerprint.Compute("""{"recipient":{"name":"Fictional B"}}"""));

    [Fact]
    public void Compute_ReturnsLowercaseSha256Hex()
    {
        var fingerprint = OrderFingerprint.Compute("""{"a":1}""");

        Assert.Equal(InboxJobLimits.FingerprintLength, fingerprint.Length);
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }

    [Fact]
    public void Compute_IsStableAcrossCalls() =>
        Assert.Equal(
            OrderFingerprint.Compute(InboxJobTestData.Payload),
            OrderFingerprint.Compute(InboxJobTestData.Payload));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"a":1,"a":2}""")]
    public void Compute_RejectsInvalidOrAmbiguousJson(string json) =>
        Assert.ThrowsAny<ArgumentException>(() => OrderFingerprint.Compute(json));
}
