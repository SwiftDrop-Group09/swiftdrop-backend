using System.Security.Cryptography;
using System.Text;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Security;

namespace SwiftDrop.Merchant.Tests.Webhooks.Security;

public sealed class HmacSignatureProviderTests
{
    [Fact]
    public void ComputeSignature_MatchesExpectedHmacSha256()
    {
        var secret = Encoding.UTF8.GetBytes("test-secret-key-32-bytes-long!!");
        var timestamp = "1731240000";
        var body = Encoding.UTF8.GetBytes("{\"eventId\":\"11111111-2222-3333-4444-555555555555\",\"status\":\"Delivered\"}");

        var signature = HmacSignatureProvider.ComputeSignature(secret, timestamp, body);

        Assert.StartsWith("sha256=", signature);
        Assert.Equal(71, signature.Length); // "sha256=" (7) + 64 hex chars = 71

        // Verify with reference HMAC computation
        var payload = Encoding.ASCII.GetBytes(timestamp + ".").Concat(body).ToArray();
        var expectedHex = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(secret, payload)).ToLowerInvariant();
        Assert.Equal(expectedHex, signature);
    }

    [Fact]
    public void VerifySignature_WithValidSignatureAndFreshTimestamp_ReturnsTrue()
    {
        var secret = Encoding.UTF8.GetBytes("secret-signing-material-12345");
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var body = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");

        var signature = HmacSignatureProvider.ComputeSignature(secret, timestamp, body);
        var isValid = HmacSignatureProvider.VerifySignature(secret, body, timestamp, signature, now);

        Assert.True(isValid);
    }

    [Fact]
    public void VerifySignature_WhenBodyTampered_ReturnsFalse()
    {
        var secret = Encoding.UTF8.GetBytes("secret-signing-material-12345");
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var body = Encoding.UTF8.GetBytes("{\"test\":\"original\"}");
        var tamperedBody = Encoding.UTF8.GetBytes("{\"test\":\"tampered\"}");

        var signature = HmacSignatureProvider.ComputeSignature(secret, timestamp, body);
        var isValid = HmacSignatureProvider.VerifySignature(secret, tamperedBody, timestamp, signature, now);

        Assert.False(isValid);
    }

    [Fact]
    public void VerifySignature_WhenTimestampStaleOver300Seconds_ReturnsFalse()
    {
        var secret = Encoding.UTF8.GetBytes("secret-signing-material-12345");
        var now = DateTimeOffset.UtcNow;
        var staleTimestamp = now.AddSeconds(-301).ToUnixTimeSeconds().ToString();
        var body = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");

        var signature = HmacSignatureProvider.ComputeSignature(secret, staleTimestamp, body);
        var isValid = HmacSignatureProvider.VerifySignature(secret, body, staleTimestamp, signature, now);

        Assert.False(isValid);
    }

    [Fact]
    public void VerifySignature_WhenTimestampFutureOver300Seconds_ReturnsFalse()
    {
        var secret = Encoding.UTF8.GetBytes("secret-signing-material-12345");
        var now = DateTimeOffset.UtcNow;
        var futureTimestamp = now.AddSeconds(301).ToUnixTimeSeconds().ToString();
        var body = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");

        var signature = HmacSignatureProvider.ComputeSignature(secret, futureTimestamp, body);
        var isValid = HmacSignatureProvider.VerifySignature(secret, body, futureTimestamp, signature, now);

        Assert.False(isValid);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("123a")]
    [InlineData("-123")]
    [InlineData("")]
    public void VerifySignature_WhenTimestampInvalid_ReturnsFalse(string invalidTimestamp)
    {
        var secret = Encoding.UTF8.GetBytes("secret-signing-material-12345");
        var body = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");

        var isValid = HmacSignatureProvider.VerifySignature(secret, body, invalidTimestamp, "sha256=abcdef", DateTimeOffset.UtcNow);

        Assert.False(isValid);
    }
}
