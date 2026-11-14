using System.Security.Cryptography;
using System.Text;

namespace SwiftDrop.Merchant.Api.Domain;

public sealed class ApiKey
{
    private ApiKey() { }

    private ApiKey(
        Guid id,
        Guid businessId,
        string keyPrefix,
        string keyHash,
        string salt,
        string scopes,
        DateTimeOffset createdAt)
    {
        Id = DomainGuard.Id(id, nameof(id));
        BusinessId = DomainGuard.Id(businessId, nameof(businessId));
        KeyPrefix = DomainGuard.Text(keyPrefix, nameof(keyPrefix), 64);
        KeyHash = DomainGuard.Text(keyHash, nameof(keyHash), 128);
        Salt = DomainGuard.Text(salt, nameof(salt), 64);
        Scopes = DomainGuard.Text(scopes, nameof(scopes), 200);
        IsActive = true;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public static (ApiKey entity, string plaintextKey) Create(
        Guid businessId,
        IEnumerable<string> requestedScopes,
        DateTimeOffset? createdAt = null)
    {
        var validatedScopes = ApiKeyScopes.ParseAndValidate(requestedScopes);
        var id = Guid.NewGuid();
        var idHex = id.ToString("N");
        var prefix = $"swd_test_{idHex}";

        var secretRandom = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var plaintextKey = $"{prefix}_{secretRandom}";

        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var keyHash = ComputeHash(plaintextKey, salt);

        var entity = new ApiKey(
            id,
            businessId,
            prefix,
            keyHash,
            salt,
            string.Join(" ", validatedScopes),
            createdAt ?? DateTimeOffset.UtcNow);

        return (entity, plaintextKey);
    }

    public bool Verify(string candidatePlaintextKey)
    {
        if (string.IsNullOrWhiteSpace(candidatePlaintextKey) || !IsActive)
        {
            return false;
        }

        // Keep previously issued eight-character-prefix keys usable. New keys
        // carry their full ID and use the documented salt || raw-key hash.
        var candidateHash = KeyPrefix.Length == "swd_test_".Length + 8
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(candidatePlaintextKey + ":" + Salt)))
            : ComputeHash(candidatePlaintextKey, Salt);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidateHash),
            Encoding.UTF8.GetBytes(KeyHash));
    }

    public void Revoke()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        RevokedAt = DateTimeOffset.UtcNow;
    }

    public bool HasScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return false;
        }

        return Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Contains(scope, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> GetScopesList() =>
        Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string ComputeHash(string plaintextKey, string salt)
    {
        var inputBytes = Convert.FromBase64String(salt).Concat(Encoding.UTF8.GetBytes(plaintextKey)).ToArray();
        var hashBytes = SHA256.HashData(inputBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public Guid Id { get; private set; }
    public Guid BusinessId { get; private set; }
    public string KeyPrefix { get; private set; } = null!;
    public string KeyHash { get; private set; } = null!;
    public string Salt { get; private set; } = null!;
    public string Scopes { get; private set; } = null!;
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
}
