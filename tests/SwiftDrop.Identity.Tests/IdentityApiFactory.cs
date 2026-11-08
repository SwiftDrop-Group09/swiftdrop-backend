using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Tests.Persistence;

namespace SwiftDrop.Identity.Tests;

internal sealed class IdentityApiFactory : WebApplicationFactory<SwiftDrop.Identity.Api.Program>
{
    private readonly string keyPath = Path.Combine(Path.GetTempPath(), "swiftdrop-identity-key-" + Guid.NewGuid().ToString("N") + ".pem");
    private readonly string? databaseConnection;
    public const string Origin = "https://identity.example.test";
    public GoogleStub Google { get; } = new();
    public MutableClock Clock { get; } = new();

    public IdentityApiFactory(IdentityPostgresFixture? database = null)
    {
        databaseConnection = database?.ConnectionString;
        using var rsa = RSA.Create(2048);
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
    }

    public HttpClient Client(bool cookies = false) => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(Origin), HandleCookies = cookies, AllowAutoRedirect = false
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = databaseConnection,
            ["Identity:Tokens:Issuer"] = Origin,
            ["Identity:Tokens:Audience"] = "swiftdrop-api",
            ["Identity:Tokens:PrivateKeyPath"] = keyPath,
            ["Identity:Tokens:KeyId"] = "test-key",
            ["Identity:Browser:AllowedOrigins:0"] = Origin,
            ["Logging:EventLog:LogLevel:Default"] = "None"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGoogleIdentityVerifier>();
            services.AddSingleton<IGoogleIdentityVerifier>(Google);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            File.Delete(keyPath);
        }
    }

    internal sealed class GoogleStub : IGoogleIdentityVerifier
    {
        public ConcurrentDictionary<string, VerifiedGoogleIdentity> Tokens { get; } = new();
        public Task<VerifiedGoogleIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Tokens.TryGetValue(idToken, out var identity) ? identity : null);
        }
    }

    internal sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
