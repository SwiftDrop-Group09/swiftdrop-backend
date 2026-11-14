using System.Security.Cryptography;
using System.Text;

namespace SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Security;

public static class HmacSignatureProvider
{
    public const int MaxClockSkewSeconds = 300;

    public static string ComputeSignature(byte[] secret, string timestamp, byte[] rawBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);
        ArgumentNullException.ThrowIfNull(rawBodyBytes);

        var prefixBytes = Encoding.ASCII.GetBytes(timestamp + ".");
        var payloadToSign = new byte[prefixBytes.Length + rawBodyBytes.Length];
        Buffer.BlockCopy(prefixBytes, 0, payloadToSign, 0, prefixBytes.Length);
        Buffer.BlockCopy(rawBodyBytes, 0, payloadToSign, prefixBytes.Length, rawBodyBytes.Length);

        var hash = HMACSHA256.HashData(secret, payloadToSign);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool VerifySignature(
        byte[] secret,
        byte[] rawBodyBytes,
        string timestamp,
        string suppliedSignature,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(timestamp) || !timestamp.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (!long.TryParse(timestamp, out var epochSeconds))
        {
            return false;
        }

        var currentTime = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        if (Math.Abs(currentTime - epochSeconds) > MaxClockSkewSeconds)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(suppliedSignature))
        {
            return false;
        }

        var expectedSignature = ComputeSignature(secret, timestamp, rawBodyBytes);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedSignature),
            Encoding.ASCII.GetBytes(suppliedSignature));
    }
}
