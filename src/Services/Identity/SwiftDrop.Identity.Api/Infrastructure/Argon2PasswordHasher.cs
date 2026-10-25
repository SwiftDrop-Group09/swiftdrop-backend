using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using SwiftDrop.Identity.Api.Application;

namespace SwiftDrop.Identity.Api.Infrastructure;

public sealed class Argon2PasswordHasher : IIdentityPasswordVerifier
{
    private const int MemorySizeKiB = 65536;
    private const int Iterations = 3;
    private const int DegreeOfParallelism = 1;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public async Task<string> HashAsync(string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = await DeriveKeyAsync(password, salt);
        cancellationToken.ThrowIfCancellationRequested();
        return $"argon2id$v=19$m={MemorySizeKiB},t={Iterations},p={DegreeOfParallelism}${Encode(salt)}${Encode(hash)}";
    }

    public async Task<bool> VerifyAsync(string password, string? encodedHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Only this application's measured, fixed Argon2id format is accepted. Never
        // let a stored parameter string allocate unbounded memory or CPU work.
        if (string.IsNullOrEmpty(password) || password.Length > 128 || encodedHash is null || encodedHash.Length > 256)
        {
            return false;
        }

        var parts = encodedHash.Split('$');
        if (parts.Length != 5 || parts[0] != "argon2id" || parts[1] != "v=19" ||
            parts[2] != $"m={MemorySizeKiB},t={Iterations},p={DegreeOfParallelism}")
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Decode(parts[3]);
            expected = Decode(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length != SaltSize || expected.Length != HashSize ||
            Encode(salt) != parts[3] || Encode(expected) != parts[4])
        {
            return false;
        }

        var actual = await DeriveKeyAsync(password, salt);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static async Task<byte[]> DeriveKeyAsync(string password, byte[] salt)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = MemorySizeKiB,
                Iterations = Iterations,
                DegreeOfParallelism = DegreeOfParallelism
            };

            return await argon2.GetBytesAsync(HashSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static string Encode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}
