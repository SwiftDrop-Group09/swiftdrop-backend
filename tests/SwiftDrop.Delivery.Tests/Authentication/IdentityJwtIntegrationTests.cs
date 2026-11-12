using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Infrastructure.Authentication;

namespace SwiftDrop.Delivery.Tests.Authentication;

public sealed class IdentityJwtIntegrationTests
{
    private const string Issuer = "https://identity.test";
    private const string Audience = "swiftdrop-api-test";
    private static readonly Guid Subject = Guid.Parse("00000000-0000-4000-8000-000000000001");

    [Fact]
    public async Task SignedIdentityCustomerTokenAuthenticatesAndMapsSubjectAndRole()
    {
        using var keys = new Keys();
        using var factory = new JwtFactory(keys);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", keys.Token());
        using var response = await client.GetAsync("/api/v1/quotes/zones");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Subject.ToString(), await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var again = await client.GetAsync("/api/v1/quotes/zones");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, keys.Requests); // Public metadata is cached, never Identity SQL/private keys.
    }

    [Theory]
    [InlineData("Courier")]
    [InlineData("Admin")]
    [InlineData("Merchant")]
    [InlineData("Dispatcher")]
    public async Task ValidHumanTokenDoesNotGrantCustomerRole(string role)
    {
        using var keys = new Keys();
        using var factory = new JwtFactory(keys);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", keys.Token(role: role));
        using var response = await client.GetAsync("/api/v1/quotes/zones");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("multiple-audiences")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("long-lifetime")]
    [InlineData("no-subject")]
    [InlineData("invalid-subject")]
    [InlineData("no-jti")]
    [InlineData("no-iat")]
    [InlineData("no-nbf")]
    [InlineData("no-exp")]
    [InlineData("roles-string")]
    [InlineData("unknown-role")]
    [InlineData("signature")]
    [InlineData("unknown-key")]
    [InlineData("unsigned")]
    public async Task InvalidTokensFailClosed(string defect)
    {
        using var keys = new Keys();
        using var factory = new JwtFactory(keys);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", keys.Token(defect));
        using var response = await client.GetAsync("/api/v1/quotes/zones");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("error_description", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("malformed")]
    [InlineData("oversize")]
    [InlineData("private-key")]
    [InlineData("empty")]
    public async Task UnavailableOrInvalidPublicKeyMetadataCannotAuthorize(string defect)
    {
        using var keys = new Keys { MetadataDefect = defect };
        using var factory = new JwtFactory(keys);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", keys.Token());
        using var response = await client.GetAsync("/api/v1/quotes/zones");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task InsecureRemoteMetadataIsRejectedWithoutFetchingIt()
    {
        using var keys = new Keys();
        using var factory = new JwtFactory(keys, "http://untrusted.test/.well-known/jwks.json");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", keys.Token());
        using var response = await client.GetAsync("/api/v1/quotes/zones");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, keys.Requests);
    }

    private sealed class JwtFactory(Keys keys, string url = "https://identity.test/.well-known/jwks.json")
        : WebApplicationFactory<SwiftDrop.Delivery.Api.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Tokens:Issuer"] = Issuer,
                ["Identity:Tokens:Audience"] = Audience,
                ["Identity:Tokens:JwksUrl"] = url,
                ["ConnectionStrings:Database"] = "",
                ["DELIVERY_DATABASE_CONNECTION_STRING"] = ""
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient(DeliveryIdentityAuthentication.JwksClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => keys);
                services.AddHttpContextAccessor();
                services.AddScoped<IBookingZoneCatalog>(provider => new PrincipalCatalog(provider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()));
            });
        }
    }

    private sealed class PrincipalCatalog(Microsoft.AspNetCore.Http.IHttpContextAccessor accessor) : IBookingZoneCatalog
    {
        public Task<IReadOnlyList<BookableZone>> ReadActiveAsync(CancellationToken cancellationToken)
        {
            var user = accessor.HttpContext!.User;
            Assert.True(user.IsInRole("Customer"));
            Assert.Equal(Subject.ToString(), user.FindFirst("sub")?.Value);
            return Task.FromResult<IReadOnlyList<BookableZone>>([new(Subject, "TESTDATA area")]);
        }
    }

    private sealed class Keys : HttpMessageHandler
    {
        private readonly RSA rsa = RSA.Create(2048);
        public string? MetadataDefect { get; init; }
        public int Requests { get; private set; }

        public string Token(string? defect = null, string role = "Customer")
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = new JwtPayload
            {
                ["iss"] = defect == "issuer" ? "https://other.test" : Issuer,
                ["aud"] = defect == "audience" ? "workload-api" : Audience,
                ["sub"] = Subject.ToString(), ["jti"] = Guid.NewGuid().ToString(),
                ["iat"] = now, ["nbf"] = now, ["exp"] = now + 900, ["roles"] = new[] { role }
            };
            switch (defect)
            {
                case "multiple-audiences": payload["aud"] = new[] { Audience, "other" }; break;
                case "expired": payload["iat"] = now - 1800; payload["nbf"] = now - 1800; payload["exp"] = now - 900; break;
                case "future": payload["iat"] = now + 300; payload["nbf"] = now + 300; payload["exp"] = now + 900; break;
                case "long-lifetime": payload["exp"] = now + 901; break;
                case "no-subject": payload.Remove("sub"); break;
                case "invalid-subject": payload["sub"] = "google-subject"; break;
                case "no-jti": payload.Remove("jti"); break;
                case "no-iat": payload.Remove("iat"); break;
                case "no-nbf": payload.Remove("nbf"); break;
                case "no-exp": payload.Remove("exp"); break;
                case "roles-string": payload["roles"] = "Customer"; break;
                case "unknown-role": payload["roles"] = new[] { "service" }; break;
            }
            var signingKey = new RsaSecurityKey(rsa) { KeyId = defect == "unknown-key" ? "unknown" : "test-key" };
            var header = defect == "unsigned" ? new JwtHeader() : new JwtHeader(new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
            if (defect == "signature")
            {
                using var other = RSA.Create(2048);
                header = new JwtHeader(new SigningCredentials(new RsaSecurityKey(other) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256));
                return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
            }
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("https://identity.test/.well-known/jwks.json", request.RequestUri!.AbsoluteUri);
            var parameters = rsa.ExportParameters(false);
            var json = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "test-key",
                n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) } } });
            json = MetadataDefect switch
            {
                "malformed" => "not-json", "oversize" => new string('x', 65537), "empty" => "{\"keys\":[]}",
                "private-key" => json.Replace("\"kty\"", "\"d\":\"private\",\"kty\""), _ => json
            };
            return Task.FromResult(new HttpResponseMessage(MetadataDefect == "offline" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent(json) });
        }

        protected override void Dispose(bool disposing) { if (disposing) rsa.Dispose(); base.Dispose(disposing); }
    }
}
