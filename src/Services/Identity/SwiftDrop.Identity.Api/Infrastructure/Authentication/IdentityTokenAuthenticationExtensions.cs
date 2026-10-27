using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using JsonWebToken = Microsoft.IdentityModel.JsonWebTokens.JsonWebToken;
using Microsoft.IdentityModel.Tokens;
using SwiftDrop.Identity.Api.Application;

namespace SwiftDrop.Identity.Api.Infrastructure.Authentication;

public static class IdentityTokenAuthenticationExtensions
{
    public static IServiceCollection AddIdentityTokenAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => new IdentityAccessTokens(configuration));
        services.AddSingleton<IIdentityAccessTokens>(provider => provider.GetRequiredService<IdentityAccessTokens>());
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IdentityAccessTokens>((options, tokens) =>
            {
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = configuration["Identity:Tokens:Issuer"] ?? "unconfigured-identity-issuer",
                    ValidateAudience = true,
                    ValidAudience = configuration["Identity:Tokens:Audience"] ?? "unconfigured-identity-audience",
                    IgnoreTrailingSlashWhenValidatingAudience = false,
                    AudienceValidator = (audiences, _, _) =>
                    {
                        var values = audiences.ToArray();
                        return values.Length == 1 && string.Equals(values[0], configuration["Identity:Tokens:Audience"], StringComparison.Ordinal);
                    },
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireAudience = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidTypes = ["JWT"],
                    TryAllIssuerSigningKeys = false,
                    IssuerSigningKeyResolver = (_, _, keyId, _) => tokens.ResolveVerificationKeys(keyId),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = "roles"
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal!;
                        var subjects = principal.FindAll(JwtRegisteredClaimNames.Sub).ToArray();
                        var issuedAt = principal.FindFirst(JwtRegisteredClaimNames.Iat)?.Value;
                        var notBefore = principal.FindFirst(JwtRegisteredClaimNames.Nbf)?.Value;
                        var expiry = principal.FindFirst(JwtRegisteredClaimNames.Exp)?.Value;
                        if (subjects.Length != 1 || !Guid.TryParse(subjects[0].Value, out var userId) || userId == Guid.Empty ||
                            string.IsNullOrWhiteSpace(principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value) ||
                            !long.TryParse(issuedAt, NumberStyles.None, CultureInfo.InvariantCulture, out var issued) ||
                            !long.TryParse(notBefore, NumberStyles.None, CultureInfo.InvariantCulture, out var validFrom) ||
                            !long.TryParse(expiry, NumberStyles.None, CultureInfo.InvariantCulture, out var expires) ||
                            expires <= issued || expires - issued > 900 || validFrom != issued ||
                            issued > DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds() ||
                            !ValidRoles(context.SecurityToken))
                        {
                            context.Fail("Invalid human access token.");
                        }

                        return Task.CompletedTask;
                    }
                };
            });
        services.AddAuthorization();
        return services;
    }

    private static bool ValidRoles(SecurityToken token)
    {
        var encoded = token switch
        {
            JsonWebToken jwt => jwt.EncodedPayload,
            JwtSecurityToken jwt => jwt.RawPayload,
            _ => null
        };
        if (encoded is null) return false;
        try
        {
            using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(encoded));
            return payload.RootElement.TryGetProperty("roles", out var roles)
                && roles.ValueKind == JsonValueKind.Array
                && roles.EnumerateArray().All(role => role.ValueKind == JsonValueKind.String
                    && role.GetString() is "Customer" or "Courier" or "Merchant" or "Dispatcher" or "Admin");
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
