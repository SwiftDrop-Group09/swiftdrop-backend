using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

namespace SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Security;

public sealed class AesGcmSecretProtector : IWebhookSecretProtector
{
    public const int KeySizeBytes = 32;
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;
    public const int CurrentKeyVersion = 1;

    private readonly byte[] _key;

    public AesGcmSecretProtector(IConfiguration configuration)
    {
        var configuredKey = configuration["WEBHOOK_ENCRYPTION_KEY_V1"]
            ?? configuration["Webhooks:EncryptionKeyV1"];

        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            _key = configuredKey.Length == 64 && IsHexString(configuredKey)
                ? Convert.FromHexString(configuredKey)
                : SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
        }
        else
        {
            _key = SHA256.HashData(Encoding.UTF8.GetBytes("swiftdrop-merchant-webhook-development-kek-v1"));
        }
    }

    public AesGcmSecretProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"Encryption key must be exactly {KeySizeBytes} bytes (256 bits).", nameof(key));
        }
        _key = key.ToArray();
    }

    public (byte[] Ciphertext, byte[] Nonce, byte[] Tag, int KeyVersion) Encrypt(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aesGcm = new AesGcm(_key, TagSizeBytes);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        return (ciphertext, nonce, tag, CurrentKeyVersion);
    }

    public byte[] Decrypt(byte[] ciphertext, byte[] nonce, byte[] tag, int keyVersion)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(tag);

        if (keyVersion != CurrentKeyVersion)
        {
            throw new NotSupportedException($"Unsupported key version '{keyVersion}'. Active key version is {CurrentKeyVersion}.");
        }

        var plaintext = new byte[ciphertext.Length];
        using var aesGcm = new AesGcm(_key, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    public (string PlaintextSecret, byte[] Ciphertext, byte[] Nonce, byte[] Tag, int KeyVersion) GenerateAndEncrypt()
    {
        var secretBytes = new byte[32];
        RandomNumberGenerator.Fill(secretBytes);

        var plaintextHex = Convert.ToHexString(secretBytes).ToLowerInvariant();
        var (ciphertext, nonce, tag, keyVersion) = Encrypt(secretBytes);

        return (plaintextHex, ciphertext, nonce, tag, keyVersion);
    }

    private static bool IsHexString(string input)
    {
        return input.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));
    }
}
