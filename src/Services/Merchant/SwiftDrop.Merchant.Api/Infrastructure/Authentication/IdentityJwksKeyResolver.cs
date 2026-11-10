using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace SwiftDrop.Merchant.Api.Infrastructure.Authentication;

/// <summary>
/// Resolves Identity's public signing keys from its internal JWKS endpoint.
/// The key set is cached briefly and refreshed when an unknown key id appears.
/// </summary>
public sealed class IdentityJwksKeyResolver(IConfiguration configuration)
{
    private readonly Lock sync = new();
    private readonly HttpClient client = new();
    private IReadOnlyList<SecurityKey> keys = [];
    private DateTimeOffset expiresAt = DateTimeOffset.MinValue;

    public TokenValidationParameters CreateValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = configuration["Identity:Tokens:Issuer"] ?? "https://identity.swiftdrop.invalid",
        ValidateAudience = true,
        ValidAudience = configuration["Identity:Tokens:Audience"] ?? "swiftdrop-api-local",
        ValidateIssuerSigningKey = true,
        RequireSignedTokens = true,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        ValidTypes = ["JWT"],
        IssuerSigningKeyResolver = (_, _, keyId, _) => Resolve(keyId),
        NameClaimType = JwtRegisteredClaimNames.Sub,
        RoleClaimType = "roles"
    };

    private IEnumerable<SecurityKey> Resolve(string? keyId)
    {
        lock (sync)
        {
            if (DateTimeOffset.UtcNow >= expiresAt ||
                (keyId is not null && keys.All(key => key.KeyId != keyId)))
            {
                var url = configuration["Identity:Tokens:JwksUrl"] ?? "http://127.0.0.2:8080/.well-known/jwks.json";
                if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var jwksUri))
                    throw new InvalidOperationException("Identity JWKS configuration is unavailable.");

                var json = client.GetStringAsync(jwksUri).GetAwaiter().GetResult();
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("keys", out var keyArray) ||
                    keyArray.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Identity JWKS response is invalid.");

                keys = keyArray.EnumerateArray()
                    .Where(key => key.TryGetProperty("kid", out _) &&
                        key.TryGetProperty("kty", out var kty) && kty.GetString() == "RSA" &&
                        key.TryGetProperty("alg", out var alg) && alg.GetString() == SecurityAlgorithms.RsaSha256)
                    .Select(key => new JsonWebKey(key.GetRawText()))
                    .Cast<SecurityKey>()
                    .ToArray();
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            }

            return keyId is null ? keys : keys.Where(key => key.KeyId == keyId).ToArray();
        }
    }
}
