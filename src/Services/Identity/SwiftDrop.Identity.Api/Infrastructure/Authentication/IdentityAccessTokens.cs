using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using SwiftDrop.Identity.Api.Application;

namespace SwiftDrop.Identity.Api.Infrastructure.Authentication;

internal sealed class IdentityAccessTokens : IIdentityAccessTokens, IDisposable
{
    private readonly Lazy<KeyRing> keys;

    public IdentityAccessTokens(IConfiguration configuration)
    {
        keys = new Lazy<KeyRing>(() => LoadKeys(configuration.GetSection("Identity:Tokens")));
    }

    public void EnsureConfigured() => _ = keys.Value;

    public string Issue(RegisteredProfile user, DateTimeOffset now)
    {
        var ring = keys.Value;
        if (!user.IsActive || user.Id == Guid.Empty ||
            user.Roles.Any(role => role is not ("Customer" or "Courier" or "Merchant" or "Dispatcher" or "Admin")))
        {
            throw new InvalidOperationException("An active human identity with valid roles is required.");
        }

        var issuedAt = now.ToUnixTimeSeconds();
        var payload = new JwtPayload
        {
            [JwtRegisteredClaimNames.Iss] = ring.Issuer,
            [JwtRegisteredClaimNames.Aud] = ring.Audience,
            [JwtRegisteredClaimNames.Sub] = user.Id.ToString("D"),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            [JwtRegisteredClaimNames.Iat] = issuedAt,
            [JwtRegisteredClaimNames.Nbf] = issuedAt,
            [JwtRegisteredClaimNames.Exp] = issuedAt + 900,
            ["roles"] = user.Roles.Distinct(StringComparer.Ordinal).ToArray()
        };
        var token = new JwtSecurityToken(new JwtHeader(new SigningCredentials(ring.SigningKey, SecurityAlgorithms.RsaSha256)), payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public object PublicKeys() => new
    {
        keys = keys.Value.VerificationKeys.Select(key =>
        {
            var parameters = key.Rsa.ExportParameters(false);
            return new
            {
                kty = "RSA",
                use = "sig",
                alg = "RS256",
                kid = key.KeyId,
                n = Base64UrlEncoder.Encode(parameters.Modulus),
                e = Base64UrlEncoder.Encode(parameters.Exponent)
            };
        }).ToArray()
    };

    internal IEnumerable<SecurityKey> ResolveVerificationKeys(string? keyId)
    {
        try
        {
            return keys.Value.VerificationKeys.Where(key => key.KeyId == keyId).ToArray();
        }
        catch (InvalidOperationException)
        {
            // Missing/malformed mounted keys must reject authentication while the
            // scaffold remains available for process-health checks.
            return [];
        }
    }

    private static KeyRing LoadKeys(IConfigurationSection configuration)
    {
        var allocated = new List<RSA>();
        try
        {
            var issuer = configuration["Issuer"];
            var audience = configuration["Audience"];
            var keyId = configuration["KeyId"];
            var privateKeyPath = configuration["PrivateKeyPath"];
            if (string.IsNullOrWhiteSpace(issuer) || !Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) ||
                issuerUri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(audience) ||
                audience.Length > 256 || !ValidKeyId(keyId) || string.IsNullOrWhiteSpace(privateKeyPath) ||
                !Path.IsPathFullyQualified(privateKeyPath))
            {
                throw new InvalidOperationException();
            }

            var signingRsa = ReadRsa(privateKeyPath, allocated);
            // Reject a public-only PEM before any account/session transaction.
            _ = signingRsa.SignHash(SHA256.HashData([]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var signingKey = new RsaSecurityKey(signingRsa) { KeyId = keyId };
            var verificationKeys = new List<RsaSecurityKey> { signingKey };
            foreach (var previous in configuration.GetSection("PreviousPublicKeyPaths").GetChildren())
            {
                if (!ValidKeyId(previous.Key) || verificationKeys.Any(key => key.KeyId == previous.Key) ||
                    string.IsNullOrWhiteSpace(previous.Value) || !Path.IsPathFullyQualified(previous.Value))
                {
                    throw new InvalidOperationException();
                }

                var previousRsa = ReadRsa(previous.Value, allocated);
                var publicRsa = RSA.Create();
                allocated.Add(publicRsa);
                publicRsa.ImportParameters(previousRsa.ExportParameters(false));
                verificationKeys.Add(new RsaSecurityKey(publicRsa) { KeyId = previous.Key });
            }

            return new KeyRing(issuer, audience, signingKey, verificationKeys, allocated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            foreach (var rsa in allocated)
            {
                rsa.Dispose();
            }

            // Do not expose key paths, PEM contents or the underlying parser error.
            throw new InvalidOperationException("Identity access-token configuration is unavailable.");
        }
    }

    private static RSA ReadRsa(string path, ICollection<RSA> allocated)
    {
        var rsa = RSA.Create();
        allocated.Add(rsa);
        var keyFile = new FileInfo(path);
        if (!keyFile.Exists || keyFile.Length > 32768)
        {
            throw new InvalidOperationException();
        }

        rsa.ImportFromPem(File.ReadAllText(path));
        if (rsa.KeySize < 2048)
        {
            throw new InvalidOperationException();
        }

        return rsa;
    }

    private static bool ValidKeyId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    void IDisposable.Dispose()
    {
        if (keys.IsValueCreated)
        {
            foreach (var rsa in keys.Value.Allocated)
            {
                rsa.Dispose();
            }
        }
    }

    private sealed record KeyRing(string Issuer, string Audience, RsaSecurityKey SigningKey,
        IReadOnlyList<RsaSecurityKey> VerificationKeys, IReadOnlyList<RSA> Allocated);
}
