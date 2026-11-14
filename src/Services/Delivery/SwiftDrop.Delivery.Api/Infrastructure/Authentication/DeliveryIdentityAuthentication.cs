using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using JsonWebToken = Microsoft.IdentityModel.JsonWebTokens.JsonWebToken;

namespace SwiftDrop.Delivery.Api.Infrastructure.Authentication;

public static class DeliveryIdentityAuthentication
{
    public const string JwksClientName = "DeliveryIdentityJwks";
    public const string WorkerServiceScheme = "DeliveryWorkerService";
    public const string WorkerServicePolicy = "DeliveryWorkerService";

    public static IServiceCollection AddDeliveryIdentityAuthentication(this IServiceCollection services)
    {
        services.AddHttpClient(JwksClientName, client => client.Timeout = TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer()
            .AddJwtBearer(WorkerServiceScheme);
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfiguration, IHttpClientFactory>((options, settings, clients) =>
            {
                var issuer = settings["Identity:Tokens:Issuer"];
                var audience = settings["Identity:Tokens:Audience"];
                var address = settings["Identity:Tokens:JwksUrl"];
                var insecure = bool.TryParse(settings["Identity:Tokens:AllowInsecureJwks"], out var allowed) && allowed;
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.SaveToken = false;
                if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || issuerUri.Scheme != "https"
                    || string.IsNullOrWhiteSpace(audience) || audience.Length > 256
                    || !Uri.TryCreate(address, UriKind.Absolute, out var jwks)
                    || !string.IsNullOrEmpty(jwks.UserInfo) || !string.IsNullOrEmpty(jwks.Query) || !string.IsNullOrEmpty(jwks.Fragment)
                    || (jwks.Scheme != "https" && !(insecure && jwks.Scheme == "http" && (jwks.IsLoopback || jwks.Host == "identity-service"))))
                {
                    options.Events = new JwtBearerEvents { OnMessageReceived = context =>
                    {
                        context.NoResult();
                        return Task.CompletedTask;
                    }};
                    return;
                }

                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(address!,
                    new IdentityJwksConfigurationRetriever(issuer!),
                    new BoundedJwksDocumentRetriever(clients.CreateClient(JwksClientName), jwks))
                {
                    AutomaticRefreshInterval = TimeSpan.FromMinutes(5),
                    RefreshInterval = TimeSpan.FromSeconds(30),
                    UseLastKnownGoodConfiguration = false
                };
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = issuer,
                    ValidateAudience = true, ValidAudience = audience,
                    AudienceValidator = (values, _, _) => values.ToArray() is [var value] && value == audience,
                    IgnoreTrailingSlashWhenValidatingAudience = false,
                    ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                    ValidateLifetime = true, RequireExpirationTime = true, RequireAudience = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["JWT"],
                    TryAllIssuerSigningKeys = false,
                    NameClaimType = JwtRegisteredClaimNames.Sub, RoleClaimType = "roles"
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (!ValidHumanClaims(context.SecurityToken)) context.Fail("Invalid human access token.");
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddOptions<JwtBearerOptions>(WorkerServiceScheme)
            .Configure<IConfiguration, IHttpClientFactory>((options, settings, clients) =>
            {
                var issuer = settings["Identity:Tokens:Issuer"];
                var address = settings["Identity:Tokens:JwksUrl"];
                var insecure = bool.TryParse(settings["Identity:Tokens:AllowInsecureJwks"], out var allowed) && allowed;
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.SaveToken = false;
                if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || issuerUri.Scheme != "https" ||
                    !Uri.TryCreate(address, UriKind.Absolute, out var jwks) ||
                    !string.IsNullOrEmpty(jwks.UserInfo) || !string.IsNullOrEmpty(jwks.Query) ||
                    !string.IsNullOrEmpty(jwks.Fragment) ||
                    (jwks.Scheme != "https" && !(insecure && jwks.Scheme == "http" &&
                        (jwks.IsLoopback || jwks.Host == "identity-service"))))
                {
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            context.NoResult();
                            return Task.CompletedTask;
                        }
                    };
                    return;
                }

                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(address!,
                    new IdentityJwksConfigurationRetriever(issuer!),
                    new BoundedJwksDocumentRetriever(clients.CreateClient(JwksClientName), jwks!))
                {
                    AutomaticRefreshInterval = TimeSpan.FromMinutes(5),
                    RefreshInterval = TimeSpan.FromSeconds(30),
                    UseLastKnownGoodConfiguration = false
                };
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = "delivery-internal",
                    AudienceValidator = (values, _, _) => values.ToArray() is [var value]
                        && value == "delivery-internal",
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireAudience = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidTypes = ["JWT"],
                    TryAllIssuerSigningKeys = false,
                    NameClaimType = JwtRegisteredClaimNames.Sub
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (!ValidWorkerServiceClaims(context.SecurityToken))
                        {
                            context.Fail("Invalid worker service token.");
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(options => options.AddPolicy(WorkerServicePolicy, policy =>
        {
            policy.AddAuthenticationSchemes(WorkerServiceScheme);
            policy.RequireAuthenticatedUser();
        }));
        return services;
    }

    private static bool ValidWorkerServiceClaims(SecurityToken token)
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
            using var document = JsonDocument.Parse(Base64UrlEncoder.Decode(encoded));
            var root = document.RootElement;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return root.TryGetProperty("sub", out var subject) && subject.GetString() == "notification-worker" &&
                root.TryGetProperty("jti", out var jti) && jti.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(jti.GetString()) &&
                root.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String &&
                scope.GetString()!.Split(' ') is ["notifications:resolve"] &&
                Number(root, "iat", out var issued) && Number(root, "nbf", out var notBefore) &&
                Number(root, "exp", out var expires) && issued >= 0 && issued <= now + 30 &&
                notBefore == issued && expires > issued && expires - issued <= 300;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool ValidHumanClaims(SecurityToken token)
    {
        var encoded = token switch { JsonWebToken jwt => jwt.EncodedPayload, JwtSecurityToken jwt => jwt.RawPayload, _ => null };
        if (encoded is null) return false;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.Decode(encoded));
            var root = document.RootElement;
            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            return names.Length == names.Distinct(StringComparer.Ordinal).Count()
                && root.TryGetProperty("sub", out var subject) && subject.ValueKind == JsonValueKind.String
                && Guid.TryParse(subject.GetString(), out var id) && id != Guid.Empty
                && root.TryGetProperty("jti", out var jti) && jti.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(jti.GetString())
                && Number(root, "iat", out var issued) && Number(root, "nbf", out var starts) && Number(root, "exp", out var expires)
                && issued >= 0 && issued <= DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()
                && starts == issued && expires > issued && expires - issued <= 900
                && root.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
                && roles.EnumerateArray().All(role => role.ValueKind == JsonValueKind.String
                    && role.GetString() is "Customer" or "Courier" or "Merchant" or "Dispatcher" or "Admin");
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException) { return false; }
    }

    private static bool Number(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value);
    }
}
