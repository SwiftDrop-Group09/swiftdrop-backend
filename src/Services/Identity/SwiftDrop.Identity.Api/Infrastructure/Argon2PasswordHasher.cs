using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace SwiftDrop.Identity.Api.Infrastructure;

public sealed class Argon2PasswordHasher
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
}
