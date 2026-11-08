using System.Security.Cryptography;
using System.Text;

namespace SwiftDrop.Identity.Api.Infrastructure.Authentication;

public static class DevelopmentSigningKey
{
    public static void Create(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Development signing keys can only be provisioned in Development.");
        }

        var configuredPath = configuration["Identity:Tokens:PrivateKeyPath"];
        if (string.IsNullOrWhiteSpace(configuredPath) || !Path.IsPathFullyQualified(configuredPath))
        {
            throw new InvalidOperationException("An explicit absolute Identity signing-key path is required.");
        }

        var keyPath = Path.GetFullPath(configuredPath);
        var parent = Path.GetDirectoryName(keyPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new InvalidOperationException("Create the private external signing-key directory before provisioning.");
        }

        for (var directory = new DirectoryInfo(parent); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // A linked ancestor could redirect a seemingly external path into
                // a repository. Require an ordinary, explicitly chosen directory.
                throw new InvalidOperationException("The signing-key directory must not contain linked ancestors.");
            }

            var gitMarker = Path.Combine(directory.FullName, ".git");
            if (File.Exists(gitMarker) || Directory.Exists(gitMarker))
            {
                throw new InvalidOperationException("Development signing keys must be stored outside Git working trees.");
            }
        }

        if (File.Exists(keyPath) || Directory.Exists(keyPath))
        {
            throw new InvalidOperationException("Refusing to replace an existing signing-key path.");
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var rsa = RSA.Create(3072);
        var encoded = Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            // CreateNew also protects against a file/symlink appearing between
            // validation and creation. Windows inherits the selected directory's ACL.
            using var stream = new FileStream(keyPath, options);
            stream.Write(encoded);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }
}
