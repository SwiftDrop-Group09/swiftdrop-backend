using System.Net.Mail;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.Apis.Auth;
using Microsoft.IdentityModel.Tokens;
using SwiftDrop.Identity.Api.Application;

[assembly: InternalsVisibleTo("SwiftDrop.Identity.Tests")]

namespace SwiftDrop.Identity.Api.Infrastructure.Google;

public sealed class GoogleIdentityVerifier : IGoogleIdentityVerifier
{
    private readonly string[] audiences;
    private readonly Func<string, GoogleJsonWebSignature.ValidationSettings, Task<GoogleJsonWebSignature.Payload>> validate;

    public GoogleIdentityVerifier(IConfiguration configuration)
        : this(configuration, GoogleJsonWebSignature.ValidateAsync)
    {
    }

    // Only the test assembly can substitute the low-level, already-verified
    // payload adapter. Runtime DI exposes no alternate issuer/key endpoint.
    internal GoogleIdentityVerifier(IConfiguration configuration,
        Func<string, GoogleJsonWebSignature.ValidationSettings, Task<GoogleJsonWebSignature.Payload>> validate)
    {
        audiences = configuration.GetSection("Identity:Google:WebClientIds").Get<string[]>()
            ?.Concat(configuration.GetSection("Identity:Google:AndroidClientIds").Get<string[]>() ?? [])
            .Distinct(StringComparer.Ordinal).ToArray() ??
            configuration.GetSection("Identity:Google:AndroidClientIds").Get<string[]>() ?? [];
        this.validate = validate;
    }

    public async Task<VerifiedGoogleIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > 10000 || audiences.Length == 0 ||
            audiences.Any(audience => string.IsNullOrWhiteSpace(audience) || audience.Length > 256))
        {
            return null;
        }

        try
        {
            var payload = await validate(idToken, new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = audiences,
                IssuedAtClockTolerance = TimeSpan.FromSeconds(30),
                ExpirationTimeClockTolerance = TimeSpan.Zero
            }).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Inspect the original OIDC field types only after the maintained
            // Google library validates this exact signed input.
            var segments = idToken.Split('.');
            if (segments.Length != 3)
            {
                return null;
            }

            using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(segments[0]));
            using var claims = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(segments[1]));
            if (header.RootElement.GetProperty("alg").GetString() != "RS256" ||
                claims.RootElement.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal)
                    .Any(group => group.Count() != 1) ||
                !HasStringValue(claims.RootElement, "iss", payload.Issuer) ||
                payload.Issuer is not ("https://accounts.google.com" or "accounts.google.com") ||
                !HasStringValue(claims.RootElement, "sub", payload.Subject) ||
                !HasStringValue(claims.RootElement, "email", payload.Email) ||
                !claims.RootElement.TryGetProperty("email_verified", out var emailVerified) || emailVerified.ValueKind != JsonValueKind.True ||
                !TryReadAudiences(claims.RootElement, out var tokenAudiences) ||
                tokenAudiences.Any(audience => !audiences.Contains(audience, StringComparer.Ordinal)) ||
                !payload.EmailVerified || string.IsNullOrWhiteSpace(payload.Subject) || payload.Subject.Length > 255 ||
                string.IsNullOrWhiteSpace(payload.Email) || payload.Email.Length > 254 ||
                !MailAddress.TryCreate(payload.Email, out var email) || email.Address != payload.Email ||
                !TryReadOptionalString(claims.RootElement, "azp", 256, out var authorizedParty) ||
                (tokenAudiences.Length > 1 && authorizedParty is null) ||
                (authorizedParty is not null && !audiences.Contains(authorizedParty, StringComparer.Ordinal)) ||
                !TryReadOptionalString(claims.RootElement, "nonce", 512, out var nonce))
            {
                return null;
            }

            var name = string.IsNullOrWhiteSpace(payload.Name) ? payload.Email : payload.Name.Trim();
            if (name.Length > 120)
            {
                name = name[..120];
            }

            return new VerifiedGoogleIdentity(payload.Subject, payload.Email, name, nonce);
        }
        catch (Exception exception) when (exception is InvalidJwtException or JsonException or FormatException or
            ArgumentException or KeyNotFoundException or InvalidOperationException or HttpRequestException or TimeoutException)
        {
            // No provider credential, decoded payload or validation diagnostics
            // leave the adapter. Bad tokens and unavailable Google fail closed.
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool HasStringValue(JsonElement payload, string claim, string? expected) =>
        payload.TryGetProperty(claim, out var property) && property.ValueKind == JsonValueKind.String &&
        string.Equals(property.GetString(), expected, StringComparison.Ordinal);

    private static bool TryReadAudiences(JsonElement payload, out string[] values)
    {
        values = [];
        if (!payload.TryGetProperty("aud", out var audience))
        {
            return false;
        }

        if (audience.ValueKind == JsonValueKind.String)
        {
            values = [audience.GetString()!];
        }
        else if (audience.ValueKind == JsonValueKind.Array)
        {
            var elements = audience.EnumerateArray().ToArray();
            if (elements.Any(element => element.ValueKind != JsonValueKind.String))
            {
                return false;
            }

            values = elements.Select(element => element.GetString()!).ToArray();
        }

        return values.Length > 0 && values.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 256);
    }

    private static bool TryReadOptionalString(JsonElement payload, string claim, int maximumLength, out string? value)
    {
        value = null;
        if (!payload.TryGetProperty(claim, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;
    }
}
