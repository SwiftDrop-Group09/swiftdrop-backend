using System.Security.Cryptography;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using SwiftDrop.Identity.Api.Infrastructure.Authentication;

namespace SwiftDrop.Identity.Tests;

public sealed class DevelopmentSigningKeyTests : IDisposable
{
    private const string Prefix = "swiftdrop-development-signing-key-";
    private readonly string parent = Path.GetFullPath(Path.GetTempPath());
    private readonly string directory;

    public DevelopmentSigningKeyTests()
    {
        directory = Path.Combine(parent, Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [Fact]
    public void ExplicitDevelopmentProvisioningCreatesExternalRsa3072PrivateKey()
    {
        var path = Path.Combine(directory, "identity.pem");
        DevelopmentSigningKey.Create(Configuration(path), Environment("Development"));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(path));
        Assert.True(rsa.KeySize >= 3072);
        var message = RandomNumberGenerator.GetBytes(32);
        var signature = rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var publicKey = RSA.Create();
        publicKey.ImportParameters(rsa.ExportParameters(false));
        Assert.True(publicKey.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Equal([path], Directory.GetFiles(directory));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void NonDevelopmentEnvironmentsCannotGenerateSigningKeys(string environment)
    {
        var path = Path.Combine(directory, "identity.pem");
        Assert.Throws<InvalidOperationException>(() => DevelopmentSigningKey.Create(Configuration(path), Environment(environment)));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("identity.pem")]
    [InlineData("../identity.pem")]
    public void MissingAndRelativePathsAreRejected(string? path)
    {
        Assert.Throws<InvalidOperationException>(() => DevelopmentSigningKey.Create(Configuration(path), Environment("Development")));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void ProvisioningDoesNotCreateMissingParentDirectories()
    {
        var missing = Path.Combine(directory, "missing");
        var path = Path.Combine(missing, "identity.pem");
        Assert.Throws<InvalidOperationException>(() => DevelopmentSigningKey.Create(Configuration(path), Environment("Development")));
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AncestorGitDirectoryAndWorktreeGitFileAreRejected(bool gitFile)
    {
        var repository = Path.Combine(directory, "repository");
        var nested = Path.Combine(repository, "nested", "private");
        Directory.CreateDirectory(nested);
        var marker = Path.Combine(repository, ".git");
        if (gitFile)
        {
            File.WriteAllText(marker, "gitdir: ../git-metadata");
        }
        else
        {
            Directory.CreateDirectory(marker);
        }

        var path = Path.Combine(nested, "identity.pem");
        Assert.Throws<InvalidOperationException>(() => DevelopmentSigningKey.Create(Configuration(path), Environment("Development")));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ExistingFilesAreNeverOverwritten()
    {
        var path = Path.Combine(directory, "identity.pem");
        File.WriteAllText(path, "existing test material");
        Assert.Throws<InvalidOperationException>(() => DevelopmentSigningKey.Create(Configuration(path), Environment("Development")));
        Assert.Equal("existing test material", File.ReadAllText(path));
    }

    [Fact]
    public void ExistingDirectoryCannotBeUsedAsKeyFile()
    {
        var path = Path.Combine(directory, "identity.pem");
        Directory.CreateDirectory(path);
        Assert.Throws<InvalidOperationException>(() => DevelopmentSigningKey.Create(Configuration(path), Environment("Development")));
        Assert.True(Directory.Exists(path));
    }

    private static IConfiguration Configuration(string? path) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Identity:Tokens:PrivateKeyPath"] = path }).Build();

    private static IHostEnvironment Environment(string name) => new TestEnvironment { EnvironmentName = name };

    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        var name = Path.GetFileName(resolved);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(parent), comparison)
            || !name.StartsWith(Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
        {
            throw new InvalidOperationException("Refusing to remove an unowned signing-key test directory.");
        }

        Directory.Delete(resolved, recursive: true);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = nameof(DevelopmentSigningKeyTests);
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
