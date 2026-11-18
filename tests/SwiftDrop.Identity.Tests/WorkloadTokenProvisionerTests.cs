using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using SwiftDrop.Identity.Api.Infrastructure.Authentication;

namespace SwiftDrop.Identity.Tests;

public sealed class WorkloadTokenProvisionerTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "swiftdrop-workload-token-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RSA signingKey = RSA.Create(2048);

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(TokenDirectory);
        File.WriteAllText(KeyPath, signingKey.ExportPkcs8PrivateKeyPem());
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DevelopmentProvisionerWritesOnlyExpectedShortLivedScopedTokens()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DevelopmentWorkloadTokenProvisioner.RunAsync(Configuration(), Environment("Development"), cancellation.Token));

        var delivery = ReadPayload("delivery.jwt");
        var merchant = ReadPayload("merchant.jwt");
        AssertToken(delivery, "delivery-internal", "notifications:resolve");
        AssertToken(merchant, "merchant-internal", "webhooks:dispatch");
        Assert.NotEqual(delivery.GetProperty("jti").GetString(), merchant.GetProperty("jti").GetString());
    }

    [Fact]
    public async Task ProvisionerRejectsNonDevelopmentEnvironmentBeforeWritingTokens()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevelopmentWorkloadTokenProvisioner.RunAsync(Configuration(), Environment("Production")));

        Assert.False(File.Exists(Path.Combine(TokenDirectory, "delivery.jwt")));
        Assert.False(File.Exists(Path.Combine(TokenDirectory, "merchant.jwt")));
    }

    private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Identity:Tokens:Issuer"] = "https://identity.test",
            ["Identity:Tokens:Audience"] = "swiftdrop-api-test",
            ["Identity:Tokens:KeyId"] = "development-test",
            ["Identity:Tokens:PrivateKeyPath"] = KeyPath,
            ["Identity:DevelopmentWorkloadTokens:Directory"] = TokenDirectory
        }).Build();

    private JsonElement ReadPayload(string fileName)
    {
        var encoded = File.ReadAllText(Path.Combine(TokenDirectory, fileName)).Trim();
        var payload = Base64UrlEncoder.DecodeBytes(encoded.Split('.')[1]);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private static void AssertToken(JsonElement token, string audience, string scope)
    {
        Assert.Equal("https://identity.test", token.GetProperty("iss").GetString());
        Assert.Equal(audience, token.GetProperty("aud").GetString());
        Assert.Equal("notification-worker", token.GetProperty("sub").GetString());
        Assert.Equal(scope, token.GetProperty("scope").GetString());
        Assert.Equal(240, token.GetProperty("exp").GetInt64() - token.GetProperty("iat").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(token.GetProperty("jti").GetString()));
    }

    private IHostEnvironment Environment(string name) => new TestHostEnvironment
    {
        EnvironmentName = name,
        ApplicationName = "SwiftDrop.Identity.Tests",
        ContentRootPath = directory,
        ContentRootFileProvider = new NullFileProvider()
    };

    private string KeyPath => Path.Combine(directory, "development.pem");
    private string TokenDirectory => Path.Combine(directory, "tokens");

    public Task DisposeAsync()
    {
        signingKey.Dispose();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
