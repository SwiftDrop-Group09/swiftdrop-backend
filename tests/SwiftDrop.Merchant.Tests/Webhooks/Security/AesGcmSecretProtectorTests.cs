using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Security;

namespace SwiftDrop.Merchant.Tests.Webhooks.Security;

public sealed class AesGcmSecretProtectorTests
{
    [Fact]
    public void EncryptAndDecrypt_RoundTripsSuccessfully()
    {
        var config = new ConfigurationBuilder().Build();
        var protector = new AesGcmSecretProtector(config);

        var original = Encoding.UTF8.GetBytes("super-secret-signing-key-12345");
        var (ciphertext, nonce, tag, keyVersion) = protector.Encrypt(original);

        Assert.Equal(original.Length, ciphertext.Length);
        Assert.Equal(12, nonce.Length);
        Assert.Equal(16, tag.Length);
        Assert.Equal(1, keyVersion);

        var decrypted = protector.Decrypt(ciphertext, nonce, tag, keyVersion);
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void GenerateAndEncrypt_ProducesHighEntropySecret()
    {
        var config = new ConfigurationBuilder().Build();
        var protector = new AesGcmSecretProtector(config);

        var (plaintextHex, ciphertext, nonce, tag, keyVersion) = protector.GenerateAndEncrypt();

        // 32 bytes hex = 64 characters (256 bits)
        Assert.Equal(64, plaintextHex.Length);
        Assert.Equal(32, ciphertext.Length);
        Assert.Equal(12, nonce.Length);
        Assert.Equal(16, tag.Length);

        var rawSecret = Convert.FromHexString(plaintextHex);
        var decrypted = protector.Decrypt(ciphertext, nonce, tag, keyVersion);
        Assert.Equal(rawSecret, decrypted);
    }

    [Fact]
    public void Decrypt_WhenTagTampered_ThrowsCryptographicException()
    {
        var config = new ConfigurationBuilder().Build();
        var protector = new AesGcmSecretProtector(config);

        var original = Encoding.UTF8.GetBytes("test-secret");
        var (ciphertext, nonce, tag, keyVersion) = protector.Encrypt(original);

        // Tamper with tag
        tag[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            protector.Decrypt(ciphertext, nonce, tag, keyVersion));
    }

    [Fact]
    public void Decrypt_WhenKeyVersionMismatch_ThrowsNotSupportedException()
    {
        var config = new ConfigurationBuilder().Build();
        var protector = new AesGcmSecretProtector(config);

        var original = Encoding.UTF8.GetBytes("test-secret");
        var (ciphertext, nonce, tag, _) = protector.Encrypt(original);

        Assert.Throws<NotSupportedException>(() =>
            protector.Decrypt(ciphertext, nonce, tag, keyVersion: 99));
    }
}
