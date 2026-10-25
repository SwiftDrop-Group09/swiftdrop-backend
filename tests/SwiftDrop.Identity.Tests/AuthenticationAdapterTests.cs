using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Api.Infrastructure;
using SwiftDrop.Identity.Api.Infrastructure.Authentication;

namespace SwiftDrop.Identity.Tests;

public sealed class AuthenticationAdapterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "swiftdrop-identity-keys-" + Guid.NewGuid().ToString("N"));
    private readonly RSA current = RSA.Create(2048);
    private readonly RSA previous = RSA.Create(2048);

    public AuthenticationAdapterTests()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "current.pem"), current.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(directory, "previous.pem"), previous.ExportSubjectPublicKeyInfoPem());
    }

    [Fact]
    public async Task Argon2VerificationAcceptsExistingHashAndRejectsIncorrectPassword()
    {
        var hasher = new Argon2PasswordHasher();
        var encoded = await hasher.HashAsync("Correct Horse Battery 7!", CancellationToken.None);

        Assert.True(await hasher.VerifyAsync("Correct Horse Battery 7!", encoded, CancellationToken.None));
        Assert.False(await hasher.VerifyAsync("Wrong Horse Battery 7!", encoded, CancellationToken.None));
        Assert.False(await hasher.VerifyAsync("Correct Horse Battery 7!", null, CancellationToken.None));
        Assert.NotEqual(encoded, await hasher.HashAsync("Correct Horse Battery 7!", CancellationToken.None));
    }

    [Theory]
    [InlineData("argon2id$v=19$m=2147483647,t=3,p=1$salt$hash")]
    [InlineData("argon2id$v=19$m=65536,t=2147483647,p=1$salt$hash")]
    [InlineData("argon2id$v=19$m=65536,t=3,p=2147483647$salt$hash")]
    [InlineData("argon2id$v=18$m=65536,t=3,p=1$salt$hash")]
    [InlineData("argon2i$v=19$m=65536,t=3,p=1$salt$hash")]
    [InlineData("argon2id$v=19$m=65536,t=3,p=1$INVALID!$INVALID!")]
    [InlineData("argon2id$v=19$m=65536,t=3,p=1$c2FsdA$aGFzaA")]
    [InlineData("")]
    public async Task MalformedOrUnboundedArgon2ParametersAreRejected(string encoded)
    {
        Assert.False(await new Argon2PasswordHasher().VerifyAsync("Correct Horse Battery 7!", encoded, CancellationToken.None));
    }

    [Fact]
    public async Task PasswordVerificationHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Argon2PasswordHasher().VerifyAsync("password", null, cancellation.Token));
    }

    [Fact]
    public void IssuedJwtHasBoundedLifetimeStableSubjectRolesAndPublicJwks()
    {
        using var services = CreateServices();
        var tokens = services.GetRequiredService<IIdentityAccessTokens>();
        var user = Profile();
        var now = DateTimeOffset.UtcNow;
        var encoded = tokens.Issue(user, now);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var jwt = handler.ReadJwtToken(encoded);

        Assert.Equal("RS256", jwt.Header.Alg);
        Assert.Equal("current", jwt.Header.Kid);
        Assert.Equal(user.Id.ToString("D"), jwt.Subject);
        Assert.Equal("https://identity.test", jwt.Issuer);
        Assert.Equal(["swiftdrop-api-test"], jwt.Audiences);
        Assert.NotEmpty(jwt.Id);
        Assert.NotEqual(jwt.Id, handler.ReadJwtToken(tokens.Issue(user, now)).Id);
        using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(encoded.Split('.')[1]));
        Assert.Equal(now.ToUnixTimeSeconds(), payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(now.ToUnixTimeSeconds(), payload.RootElement.GetProperty("nbf").GetInt64());
        Assert.Equal(900, payload.RootElement.GetProperty("exp").GetInt64() - payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(JsonValueKind.Array, payload.RootElement.GetProperty("roles").ValueKind);

        var principal = handler.ValidateToken(encoded, Validation(services), out _);
        Assert.Equal(user.Id.ToString("D"), principal.Identity!.Name);
        Assert.True(principal.IsInRole("Customer"));
        Assert.True(principal.IsInRole("Courier"));

        using var jwks = JsonDocument.Parse(JsonSerializer.Serialize(tokens.PublicKeys()));
        var key = Assert.Single(jwks.RootElement.GetProperty("keys").EnumerateArray());
        Assert.Equal(["alg", "e", "kid", "kty", "n", "use"], key.EnumerateObject().Select(property => property.Name).Order().ToArray());
        var publicKey = new JsonWebKey(key.GetRawText());
        var publicValidation = Validation(services).Clone();
        publicValidation.IssuerSigningKeyResolver = null;
        publicValidation.IssuerSigningKey = publicKey;
        Assert.NotNull(handler.ValidateToken(encoded, publicValidation, out _));
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("audienceSlash")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("key")]
    [InlineData("algorithm")]
    [InlineData("signature")]
    public void WrongIssuerAudienceTimeKeyAlgorithmAndSignatureAreRejected(string fault)
    {
        using var services = CreateServices();
        var now = DateTimeOffset.UtcNow;
        var payload = new JwtPayload
        {
            ["iss"] = fault == "issuer" ? "https://foreign.test" : "https://identity.test",
            ["aud"] = fault == "audience" ? "foreign-api" : fault == "audienceSlash" ? "swiftdrop-api-test/" : "swiftdrop-api-test",
            ["sub"] = Guid.NewGuid().ToString("D"),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.AddMinutes(fault == "future" ? 2 : -1).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(fault == "expired" ? -2 : 15).ToUnixTimeSeconds(),
            ["roles"] = new[] { "Customer" },
            ["jti"] = Guid.NewGuid().ToString("N")
        };
        using var rogue = RSA.Create(2048);
        SecurityKey signing = fault == "algorithm"
            ? new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32))
            : new RsaSecurityKey(fault == "signature" ? rogue : current);
        signing.KeyId = fault == "key" ? "unknown" : "current";
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var encoded = handler.WriteToken(new JwtSecurityToken(new JwtHeader(new SigningCredentials(signing,
            fault == "algorithm" ? SecurityAlgorithms.HmacSha256 : SecurityAlgorithms.RsaSha256)), payload));

        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(encoded, Validation(services), out _));
    }

    [Fact]
    public void PreviousPublicKeyKeepsUnexpiredTokensVerifiableAfterRotation()
    {
        using var services = CreateServices(includePrevious: true);
        var payload = new JwtPayload("https://identity.test", "swiftdrop-api-test", null,
            DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddMinutes(10));
        var previousKey = new RsaSecurityKey(previous) { KeyId = "previous" };
        var encoded = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(previousKey, SecurityAlgorithms.RsaSha256)), payload));

        Assert.NotNull(new JwtSecurityTokenHandler().ValidateToken(encoded, Validation(services), out _));
        using var jwks = JsonDocument.Parse(JsonSerializer.Serialize(services.GetRequiredService<IIdentityAccessTokens>().PublicKeys()));
        Assert.Equal(2, jwks.RootElement.GetProperty("keys").GetArrayLength());
    }

    [Fact]
    public void UnconfiguredKeysAreLazyAndFailClosed()
    {
        using var services = new ServiceCollection().AddLogging()
            .AddIdentityTokenAuthentication(new ConfigurationBuilder().Build()).BuildServiceProvider();
        var tokens = services.GetRequiredService<IIdentityAccessTokens>();
        Assert.NotNull(Validation(services));
        Assert.Throws<InvalidOperationException>(tokens.EnsureConfigured);
        Assert.Throws<InvalidOperationException>(() => tokens.Issue(Profile(), DateTimeOffset.UtcNow));
        Assert.Empty(Validation(services).IssuerSigningKeyResolver!("", null!, "current", Validation(services)));
    }

    [Fact]
    public void PublicOnlySigningKeyAndWeakRsaAreRejectedBeforeIssuance()
    {
        File.WriteAllText(Path.Combine(directory, "current.pem"), current.ExportSubjectPublicKeyInfoPem());
        using (var publicOnlyServices = CreateServices())
        {
            Assert.Throws<InvalidOperationException>(publicOnlyServices.GetRequiredService<IIdentityAccessTokens>().EnsureConfigured);
        }

        using var weak = RSA.Create(1024);
        File.WriteAllText(Path.Combine(directory, "current.pem"), weak.ExportPkcs8PrivateKeyPem());
        using var weakServices = CreateServices();
        Assert.Throws<InvalidOperationException>(weakServices.GetRequiredService<IIdentityAccessTokens>().EnsureConfigured);
    }

    private ServiceProvider CreateServices(bool includePrevious = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["Identity:Tokens:Issuer"] = "https://identity.test",
            ["Identity:Tokens:Audience"] = "swiftdrop-api-test",
            ["Identity:Tokens:KeyId"] = "current",
            ["Identity:Tokens:PrivateKeyPath"] = Path.Combine(directory, "current.pem")
        };
        if (includePrevious)
        {
            values["Identity:Tokens:PreviousPublicKeyPaths:previous"] = Path.Combine(directory, "previous.pem");
        }

        return new ServiceCollection().AddLogging()
            .AddIdentityTokenAuthentication(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider();
    }

    private static TokenValidationParameters Validation(IServiceProvider services) =>
        services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;

    private static RegisteredProfile Profile() =>
        new(Guid.NewGuid(), "customer@example.test", "Customer", null, null, ["Customer", "Courier"], true);

    public void Dispose()
    {
        current.Dispose();
        previous.Dispose();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            File.Delete(file);
        }

        Directory.Delete(directory);
    }
}
