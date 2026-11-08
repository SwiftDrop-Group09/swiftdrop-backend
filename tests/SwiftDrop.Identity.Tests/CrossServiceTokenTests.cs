using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

using JsonWebToken = Microsoft.IdentityModel.JsonWebTokens.JsonWebToken;

using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Authentication;
using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Tests.Persistence;

namespace SwiftDrop.Identity.Tests;

// The downstream verifier exists only in this contract harness. Normal Delivery
// hosts keep their fail-closed handler pending integration by the Delivery owner.
[Collection(IdentityPostgresCollection.Name)]
public sealed class CrossServiceTokenTests(IdentityPostgresFixture database)
{
    private const string Password = "Contract Test Password 7!";

    [Fact]
    public async Task RealLoginAndHttpJwksAuthorizeTheSameCustomerInUnchangedDeliveryEntry()
    {
        using var identity = new IdentityApiFactory(database);
        var login = await RegisterAndLoginAsync(identity);
        using var delivery = await DeliveryContractFactory.FromIdentityAsync(identity);
        using var client = delivery.CreateClient();
        using var request = delivery.QuoteRequest(login.AccessToken);
        request.Headers.Add("X-User-ID", Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-User-Roles", "Admin,Courier");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(login.User.Id, Assert.Single(delivery.Store.Saved).OwnerId);
        Assert.Equal(new ObservedIdentity(login.User.Id, true, false, false), delivery.Observed);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, delivery.Store.Reads);
        var encoded = new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken);
        Assert.Equal("RS256", encoded.Header.Alg);
        Assert.Equal(login.User.Id.ToString("D"), encoded.Subject);
        Assert.Equal(["Customer"], encoded.Claims.Where(claim => claim.Type == "roles").Select(claim => claim.Value));
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("audience-slash")]
    [InlineData("extra-audience")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("excessive-lifetime")]
    [InlineData("unknown-kid")]
    [InlineData("missing-kid")]
    [InlineData("wrong-signature")]
    [InlineData("algorithm")]
    [InlineData("unsigned")]
    [InlineData("missing-sub")]
    [InlineData("invalid-sub")]
    [InlineData("missing-jti")]
    [InlineData("missing-iat")]
    [InlineData("missing-roles")]
    [InlineData("roles-string")]
    [InlineData("roles-mixed")]
    [InlineData("unknown-role")]
    [InlineData("workload-audience")]
    public async Task InvalidHumanTokenIsRejectedBeforeDeliveryStorage(string fault)
    {
        using var identity = new IdentityApiFactory(database);
        var login = await RegisterAndLoginAsync(identity);
        using var delivery = await DeliveryContractFactory.FromIdentityAsync(identity);
        using var client = delivery.CreateClient();
        using var request = delivery.QuoteRequest(SignedVariant(identity, login.AccessToken, fault));

        using var response = await client.SendAsync(request);

        await AssertSafeDenialAsync(response, HttpStatusCode.Unauthorized, login);
        Assert.Empty(delivery.Store.Saved);
        Assert.Equal(0, delivery.Store.Reads);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Courier")]
    public async Task CustomerCannotImpersonatePrivilegedRoleByChangingJwtPayload(string role)
    {
        using var identity = new IdentityApiFactory(database);
        var login = await RegisterAndLoginAsync(identity);
        var parts = login.AccessToken.Split('.');
        var payload = JwtPayload.Deserialize(Base64UrlEncoder.Decode(parts[1]));
        payload["roles"] = new[] { role };
        parts[1] = Base64UrlEncoder.Encode(payload.SerializeToJson());
        using var delivery = await DeliveryContractFactory.FromIdentityAsync(identity);
        using var client = delivery.CreateClient();
        using var request = delivery.QuoteRequest(string.Join('.', parts));

        using var response = await client.SendAsync(request);

        await AssertSafeDenialAsync(response, HttpStatusCode.Unauthorized, login);
        Assert.Empty(delivery.Store.Saved);
        Assert.Equal(0, delivery.Store.Reads);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Courier")]
    public async Task AuthenticatedNonCustomerCannotUseCustomerQuoteEntry(string role)
    {
        using var identity = new IdentityApiFactory(database);
        var login = await RegisterAndLoginAsync(identity);
        using var delivery = await DeliveryContractFactory.FromIdentityAsync(identity);
        using var client = delivery.CreateClient();
        using var request = delivery.QuoteRequest(SignedVariant(identity, login.AccessToken, "role", role));

        using var response = await client.SendAsync(request);

        await AssertSafeDenialAsync(response, HttpStatusCode.Forbidden, login);
        Assert.Empty(delivery.Store.Saved);
        Assert.Equal(0, delivery.Store.Reads);
    }

    [Fact]
    public async Task ActiveHumanWithEmptyRolesIsAuthenticatedWithoutCustomerPrivilege()
    {
        using var identity = new IdentityApiFactory(database);
        var login = await RegisterAndLoginAsync(identity);
        var encoded = identity.Services.GetRequiredService<IIdentityAccessTokens>()
            .Issue(login.User with { Roles = [] }, DateTimeOffset.UtcNow);
        using var delivery = await DeliveryContractFactory.FromIdentityAsync(identity);
        using var client = delivery.CreateClient();
        using var request = delivery.QuoteRequest(encoded);

        using var response = await client.SendAsync(request);

        await AssertSafeDenialAsync(response, HttpStatusCode.Forbidden, login);
        Assert.Equal(new ObservedIdentity(login.User.Id, false, false, false), delivery.Observed);
        Assert.Empty(delivery.Store.Saved);
        Assert.Equal(0, delivery.Store.Reads);
    }

    [Theory]
    [InlineData("headers")]
    [InlineData("api-key")]
    [InlineData("google-token")]
    public async Task ForwardedIdentityApiKeysAndGoogleTokensCannotEstablishHumanIdentity(string credential)
    {
        using var identity = new IdentityApiFactory(database);
        using var delivery = await DeliveryContractFactory.FromIdentityAsync(identity);
        using var client = delivery.CreateClient();
        using var request = delivery.QuoteRequest(credential == "google-token" ? "untrusted.google.id-token" : null);
        request.Headers.Add("X-User-ID", Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-User-Roles", "Customer,Admin");
        if (credential == "api-key") request.Headers.Add("X-API-Key", "swd_test_fictional_not-a-human-credential");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Empty(delivery.Store.Saved);
        Assert.Equal(0, delivery.Store.Reads);
    }

    [Fact]
    public async Task RetainedPublicKeyVerifiesPreRotationTokenAlongsideNewSigningKey()
    {
        using var identity = new IdentityApiFactory(database);
        var login = await RegisterAndLoginAsync(identity);
        using var identityClient = identity.Client();
        using var publicResponse = await identityClient.GetAsync("/.well-known/jwks.json");
        var oldJwks = await publicResponse.Content.ReadAsStringAsync();
        using var replacementRsa = RSA.Create(2048);
        var keyDirectory = Path.Combine(Path.GetTempPath(), "swiftdrop-identity-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyDirectory);
        var newKey = Path.Combine(keyDirectory, "current.pem");
        var retainedKey = Path.Combine(keyDirectory, "previous.pem");
        try
        {
            await File.WriteAllTextAsync(newKey, replacementRsa.ExportPkcs8PrivateKeyPem());
            using var oldRsa = RSA.Create();
            var oldJwk = new JsonWebKeySet(oldJwks).Keys.Single();
            oldRsa.ImportParameters(new RSAParameters
            {
                Modulus = Base64UrlEncoder.DecodeBytes(oldJwk.N),
                Exponent = Base64UrlEncoder.DecodeBytes(oldJwk.E)
            });
            await File.WriteAllTextAsync(retainedKey, oldRsa.ExportSubjectPublicKeyInfoPem());
            using var rotated = identity.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Identity:Tokens:KeyId"] = "rotated-key",
                    ["Identity:Tokens:PrivateKeyPath"] = newKey,
                    ["Identity:Tokens:PreviousPublicKeyPaths:test-key"] = retainedKey
                })));
            using var rotatedClient = rotated.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri(IdentityApiFactory.Origin),
                HandleCookies = false
            });
            using var jwksResponse = await rotatedClient.GetAsync("/.well-known/jwks.json");
            Assert.Equal(HttpStatusCode.OK, jwksResponse.StatusCode);
            var rotatedJwks = await jwksResponse.Content.ReadAsStringAsync();
            Assert.Equal(2, new JsonWebKeySet(rotatedJwks).Keys.Count);
            var newToken = rotated.Services.GetRequiredService<IIdentityAccessTokens>().Issue(login.User, DateTimeOffset.UtcNow);
            Assert.Equal("rotated-key", new JwtSecurityTokenHandler().ReadJwtToken(newToken).Header.Kid);
            using var delivery = new DeliveryContractFactory(rotatedJwks);
            using var client = delivery.CreateClient();
            foreach (var token in new[] { login.AccessToken, newToken })
            {
                using var request = delivery.QuoteRequest(token);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            Assert.Equal(2, delivery.Store.Saved.Count);
            Assert.All(delivery.Store.Saved, quote => Assert.Equal(login.User.Id, quote.OwnerId));
        }
        finally
        {
            File.Delete(newKey);
            File.Delete(retainedKey);
            Directory.Delete(keyDirectory);
        }
    }

    private static async Task<LoginResult> RegisterAndLoginAsync(IdentityApiFactory identity)
    {
        using var client = identity.Client();
        var email = "cross-service-" + Guid.NewGuid().ToString("N") + "@example.test";
        using var registration = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = Password,
            fullName = "Contract Customer"
        });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return Assert.IsType<LoginResult>(await login.Content.ReadFromJsonAsync<LoginResult>());
    }

    private static string SignedVariant(IdentityApiFactory identity, string original, string fault, string? role = null)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(original);
        var payload = JwtPayload.Deserialize(jwt.Payload.SerializeToJson());
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        switch (fault)
        {
            case "issuer": payload["iss"] = "https://foreign.example.test"; break;
            case "audience": payload["aud"] = "foreign-api"; break;
            case "audience-slash": payload["aud"] = "swiftdrop-api/"; break;
            case "extra-audience": payload["aud"] = new[] { "swiftdrop-api", "foreign-api" }; break;
            case "workload-audience": payload["aud"] = "delivery-internal"; break;
            case "expired": payload["iat"] = now - 1900; payload["nbf"] = now - 1900; payload["exp"] = now - 1000; break;
            case "future": payload["iat"] = now + 120; payload["nbf"] = now + 120; payload["exp"] = now + 1020; break;
            case "excessive-lifetime": payload["exp"] = now + 3600; break;
            case "missing-sub": payload.Remove("sub"); break;
            case "invalid-sub": payload["sub"] = "google-provider-subject"; break;
            case "missing-jti": payload.Remove("jti"); break;
            case "missing-iat": payload.Remove("iat"); break;
            case "missing-roles": payload.Remove("roles"); break;
            case "roles-string": payload["roles"] = "Customer"; break;
            case "roles-mixed": payload["roles"] = new object[] { "Customer", 7 }; break;
            case "unknown-role": payload["roles"] = new[] { "customer" }; break;
            case "role": payload["roles"] = new[] { role! }; break;
        }

        if (fault == "unsigned") return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(), payload));
        using var rsa = RSA.Create();
        if (fault == "wrong-signature") rsa.KeySize = 2048;
        else rsa.ImportFromPem(File.ReadAllText(identity.Services.GetRequiredService<IConfiguration>()["Identity:Tokens:PrivateKeyPath"]!));
        var key = new RsaSecurityKey(rsa) { KeyId = fault == "unknown-kid" ? "untrusted-key" : jwt.Header.Kid };
        var header = new JwtHeader(new SigningCredentials(key,
            fault == "algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256));
        if (fault == "missing-kid") header.Remove("kid");
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private static async Task AssertSafeDenialAsync(HttpResponseMessage response, HttpStatusCode expected, LoginResult login)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(login.AccessToken, body);
        Assert.DoesNotContain(login.User.Email, body);
        Assert.DoesNotContain(Password, body);
        Assert.DoesNotContain("SecurityToken", body);
        Assert.DoesNotContain("PRIVATE KEY", body);
    }

    private sealed record LoginResult(string AccessToken, RegisteredProfile User);
    private sealed record ObservedIdentity(Guid? UserId, bool Customer, bool Admin, bool Courier);

    private sealed class DeliveryContractFactory(string publicJwks) : WebApplicationFactory<SwiftDrop.Delivery.Api.Program>
    {
        private const string Scheme = "IdentityContractJwt";
        public RecordingQuoteStore Store { get; } = new();
        public ObservedIdentity? Observed { get; private set; }

        public static async Task<DeliveryContractFactory> FromIdentityAsync(IdentityApiFactory identity)
        {
            using var client = identity.Client();
            using var response = await client.GetAsync("/.well-known/jwks.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return new DeliveryContractFactory(await response.Content.ReadAsStringAsync());
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = "",
                ["DELIVERY_DATABASE_CONNECTION_STRING"] = "",
                ["Logging:EventLog:LogLevel:Default"] = "None"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IQuoteStore>();
                services.AddSingleton<IQuoteStore>(Store);
                services.RemoveAll<ICurrentCustomer>();
                services.AddScoped<ICurrentCustomer>(provider => new ObservingCustomer(
                    provider.GetRequiredService<IHttpContextAccessor>(), value => Observed = value));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = Scheme;
                    options.DefaultChallengeScheme = Scheme;
                    options.DefaultForbidScheme = Scheme;
                }).AddJwtBearer(Scheme, options =>
                {
                    options.MapInboundClaims = false;
                    options.IncludeErrorDetails = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = IdentityApiFactory.Origin,
                        ValidateAudience = true,
                        ValidAudience = "swiftdrop-api",
                        IgnoreTrailingSlashWhenValidatingAudience = false,
                        AudienceValidator = (audiences, _, _) => audiences.ToArray() is ["swiftdrop-api"],
                        ValidateIssuerSigningKey = true,
                        RequireSignedTokens = true,
                        IssuerSigningKeys = new JsonWebKeySet(publicJwks).GetSigningKeys(),
                        TryAllIssuerSigningKeys = false,
                        IssuerSigningKeyResolver = (_, _, keyId, _) => string.IsNullOrWhiteSpace(keyId) ? [] :
                            new JsonWebKeySet(publicJwks).GetSigningKeys().Where(key => key.KeyId == keyId),
                        ValidateLifetime = true,
                        RequireExpirationTime = true,
                        RequireAudience = true,
                        ClockSkew = TimeSpan.FromSeconds(30),
                        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                        ValidTypes = ["JWT"],
                        NameClaimType = "sub",
                        RoleClaimType = "roles"
                    };
                    options.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = context =>
                        {
                            var principal = context.Principal!;
                            var subject = principal.FindAll("sub").Select(claim => claim.Value).ToArray();
                            var issuedAt = principal.FindFirst("iat")?.Value;
                            var validFrom = principal.FindFirst("nbf")?.Value;
                            var expiry = principal.FindFirst("exp")?.Value;
                            if (subject is not [var identifier] || !Guid.TryParse(identifier, out var userId) || userId == Guid.Empty ||
                                string.IsNullOrWhiteSpace(principal.FindFirst("jti")?.Value) || !ValidRoleArray(context.SecurityToken) ||
                                !long.TryParse(issuedAt, NumberStyles.None, CultureInfo.InvariantCulture, out var issued) ||
                                !long.TryParse(validFrom, NumberStyles.None, CultureInfo.InvariantCulture, out var starts) ||
                                !long.TryParse(expiry, NumberStyles.None, CultureInfo.InvariantCulture, out var expires) ||
                                expires <= issued || expires - issued > 900 || starts != issued ||
                                issued > DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds())
                            {
                                context.Fail("Invalid human access token.");
                            }

                            return Task.CompletedTask;
                        }
                    };
                });
            });
        }

        private static bool ValidRoleArray(SecurityToken token)
        {
            var encoded = token switch
            {
                JsonWebToken jwt => jwt.EncodedPayload,
                JwtSecurityToken jwt => jwt.RawPayload,
                _ => null
            };
            if (encoded is null) return false;
            using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(encoded));
            return payload.RootElement.TryGetProperty("roles", out var roles)
                && roles.ValueKind == JsonValueKind.Array
                && roles.EnumerateArray().All(role => role.ValueKind == JsonValueKind.String
                    && role.GetString() is "Customer" or "Courier" or "Merchant" or "Dispatcher" or "Admin");
        }

        public HttpRequestMessage QuoteRequest(string? token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/quotes")
            {
                Content = JsonContent.Create(new
                {
                    pickupAddress = new { street = "Fictional pickup", city = "Colombo", postalCode = "00100", zoneId = Store.Pickup.ToString("D") },
                    deliveryAddress = new { street = "Fictional delivery", city = "Kandy", postalCode = "20000", zoneId = Store.Delivery.ToString("D") },
                    packageDetails = new { weightKg = 2m, size = "Small" },
                    recipient = new { name = "Fictional Recipient", phoneNumber = "0000000000" }
                })
            };
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }
    }

    private sealed class ObservingCustomer(IHttpContextAccessor accessor, Action<ObservedIdentity> observe) : ICurrentCustomer
    {
        private readonly ClaimsCurrentCustomer adapter = new(accessor);
        public bool IsAuthenticated => adapter.IsAuthenticated;
        public bool IsCustomer => adapter.IsCustomer;
        public Guid? CustomerId
        {
            get
            {
                var id = adapter.CustomerId;
                var principal = accessor.HttpContext!.User;
                observe(new ObservedIdentity(id, adapter.IsCustomer, principal.IsInRole("Admin"), principal.IsInRole("Courier")));
                return id;
            }
        }
    }

    private sealed class RecordingQuoteStore : IQuoteStore
    {
        public Guid Pickup { get; } = Guid.NewGuid();
        public Guid Delivery { get; } = Guid.NewGuid();
        public List<Quote> Saved { get; } = [];
        public int Reads { get; private set; }

        public Task<QuotePricingData> ReadPricingAsync(Guid pickupZoneId, Guid deliveryZoneId, ParcelSize size, CancellationToken cancellationToken)
        {
            Reads++;
            Assert.Equal(Pickup, pickupZoneId);
            Assert.Equal(Delivery, deliveryZoneId);
            return Task.FromResult(new QuotePricingData(true, true,
                [new PricingTier(Guid.NewGuid(), Delivery, size, "contract-v1", 450m, 60m)]));
        }

        public Task SaveAsync(Quote quote, CancellationToken cancellationToken)
        {
            Saved.Add(quote);
            return Task.CompletedTask;
        }
    }
}
