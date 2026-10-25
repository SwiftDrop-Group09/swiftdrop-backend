using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using SwiftDrop.Identity.Api.Infrastructure.Google;

namespace SwiftDrop.Identity.Tests;

public sealed class GoogleVerifierTests : IAsyncLifetime
{
    private readonly RSA rsa = RSA.Create(2048);
    private WebApplication server = null!;
    private string certificatesUrl = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        server = builder.Build();
        var publicKey = rsa.ExportParameters(false);
        server.MapGet("/keys", () => Results.Json(new
        {
            keys = new[]
            {
                new { kty = "RSA", kid = "google-test", alg = "RS256", use = "sig",
                    n = Base64UrlEncoder.Encode(publicKey.Modulus), e = Base64UrlEncoder.Encode(publicKey.Exponent) }
            }
        }));
        await server.StartAsync();
        certificatesUrl = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/keys";
    }

    [Theory]
    [InlineData("web-client.apps.googleusercontent.com")]
    [InlineData("android-client.apps.googleusercontent.com")]
    public async Task SignedGoogleIdentityPreservesStableSubjectAndNonceForConfiguredPlatforms(string audience)
    {
        var result = await Verifier().VerifyAsync(Token(new Dictionary<string, object>
        {
            ["aud"] = audience,
            ["azp"] = "android-client.apps.googleusercontent.com",
            ["nonce"] = "server-bound-nonce"
        }), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("stable-google-subject", result.Subject);
        Assert.Equal("customer@example.test", result.Email);
        Assert.Equal("Customer One", result.FullName);
        Assert.Equal("server-bound-nonce", result.Nonce);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("missingAudience")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("unverifiedEmail")]
    [InlineData("missingSubject")]
    [InlineData("invalidEmail")]
    [InlineData("authorizedParty")]
    [InlineData("nonceType")]
    [InlineData("signature")]
    [InlineData("algorithm")]
    [InlineData("subjectType")]
    [InlineData("emailType")]
    [InlineData("issuerType")]
    [InlineData("verifiedEmailType")]
    [InlineData("audienceType")]
    [InlineData("audienceElementType")]
    [InlineData("multipleAudiencesMissingParty")]
    [InlineData("multipleAudiencesForeignParty")]
    [InlineData("multipleAudiencesForeignAudience")]
    public async Task InvalidSignedGoogleClaimsAndSignaturesFailClosed(string fault)
    {
        var changes = new Dictionary<string, object>();
        switch (fault)
        {
            case "issuer": changes["iss"] = "https://attacker.test"; break;
            case "audience": changes["aud"] = "foreign-client.apps.googleusercontent.com"; break;
            case "expired": changes["exp"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(); break;
            case "future": changes["iat"] = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds(); break;
            case "unverifiedEmail": changes["email_verified"] = false; break;
            case "missingSubject": changes["sub"] = ""; break;
            case "invalidEmail": changes["email"] = "not-an-email"; break;
            case "authorizedParty": changes["azp"] = "attacker-client"; break;
            case "nonceType": changes["nonce"] = 123; break;
            case "subjectType": changes["sub"] = 123; break;
            case "emailType": changes["email"] = 123; break;
            case "issuerType": changes["iss"] = 123; break;
            case "verifiedEmailType": changes["email_verified"] = "true"; break;
            case "audienceType": changes["aud"] = 123; break;
            case "audienceElementType": changes["aud"] = new object[] { "web-client.apps.googleusercontent.com", 123 }; break;
            case "multipleAudiencesMissingParty":
                changes["aud"] = new[] { "web-client.apps.googleusercontent.com", "android-client.apps.googleusercontent.com" }; break;
            case "multipleAudiencesForeignParty":
                changes["aud"] = new[] { "web-client.apps.googleusercontent.com", "android-client.apps.googleusercontent.com" };
                changes["azp"] = "foreign-client.apps.googleusercontent.com"; break;
            case "multipleAudiencesForeignAudience":
                changes["aud"] = new[] { "web-client.apps.googleusercontent.com", "foreign-client.apps.googleusercontent.com" };
                changes["azp"] = "web-client.apps.googleusercontent.com"; break;
        }

        Assert.Null(await Verifier().VerifyAsync(Token(changes, fault), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredArrayAudiencesRequireAuthorizedPartyWhenMultiple(bool multiple)
    {
        var changes = new Dictionary<string, object>
        {
            ["aud"] = multiple
                ? new[] { "web-client.apps.googleusercontent.com", "android-client.apps.googleusercontent.com" }
                : new[] { "web-client.apps.googleusercontent.com" }
        };
        if (multiple)
        {
            changes["azp"] = "web-client.apps.googleusercontent.com";
        }

        Assert.NotNull(await Verifier().VerifyAsync(Token(changes), CancellationToken.None));
    }

    [Fact]
    public async Task GoogleConfigurationAndProviderOutagesFailClosed()
    {
        var empty = new GoogleIdentityVerifier(new ConfigurationBuilder().Build());
        Assert.Null(await empty.VerifyAsync(Token([]), CancellationToken.None));
        var unavailable = new GoogleIdentityVerifier(Configuration(), (_, _) =>
            Task.FromException<GoogleJsonWebSignature.Payload>(new HttpRequestException("provider unavailable")));
        Assert.Null(await unavailable.VerifyAsync(Token([]), CancellationToken.None));
    }

    [Fact]
    public async Task ProductionGoogleVerifierRejectsMalformedCredentials()
    {
        Assert.Null(await new GoogleIdentityVerifier(Configuration()).VerifyAsync("malformed", CancellationToken.None));
    }

    [Fact]
    public async Task ProviderCancellationIsNotConvertedIntoAuthenticatedIdentity()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Verifier().VerifyAsync(Token([]), cancellation.Token));
    }

    private GoogleIdentityVerifier Verifier() => new(Configuration(), (token, settings) =>
    {
        var options = new SignedTokenVerificationOptions
        {
            CertificatesUrl = certificatesUrl,
            IssuedAtClockTolerance = settings.IssuedAtClockTolerance,
            ExpiryClockTolerance = settings.ExpirationTimeClockTolerance
        };
        foreach (var audience in settings.Audience)
        {
            options.TrustedAudiences.Add(audience);
        }

        options.TrustedIssuers.Add("https://accounts.google.com");
        options.TrustedIssuers.Add("accounts.google.com");
        return JsonWebSignature.VerifySignedTokenAsync<GoogleJsonWebSignature.Payload>(token, options);
    });

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Identity:Google:WebClientIds:0"] = "web-client.apps.googleusercontent.com",
        ["Identity:Google:AndroidClientIds:0"] = "android-client.apps.googleusercontent.com"
    }).Build();

    private string Token(Dictionary<string, object> changes, string? fault = null)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new JwtPayload
        {
            ["iss"] = "https://accounts.google.com",
            ["aud"] = "web-client.apps.googleusercontent.com",
            ["sub"] = "stable-google-subject",
            ["iat"] = now.AddSeconds(-1).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds(),
            ["email"] = "customer@example.test",
            ["email_verified"] = true,
            ["name"] = "Customer One"
        };
        foreach (var (name, value) in changes)
        {
            payload[name] = value;
        }

        if (fault == "missingAudience")
        {
            payload.Remove("aud");
        }

        using var rogue = RSA.Create(2048);
        SecurityKey key = fault == "algorithm"
            ? new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32))
            : new RsaSecurityKey(fault == "signature" ? rogue : rsa);
        key.KeyId = "google-test";
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(
            new SigningCredentials(key, fault == "algorithm" ? SecurityAlgorithms.HmacSha256 : SecurityAlgorithms.RsaSha256)), payload));
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        rsa.Dispose();
    }
}
