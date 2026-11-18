using System.Security.Cryptography;
using System.Text;

namespace SwiftDrop.Identity.Api.Infrastructure.Authentication;

/// <summary>Renews local-only Notification Worker service tokens outside HTTP request handling.</summary>
public static class DevelopmentWorkloadTokenProvisioner
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    public static async Task RunAsync(IConfiguration configuration, IHostEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Workload-token provisioning is available only in Development.");
        }

        var directory = ResolvePrivateDirectory(configuration["Identity:DevelopmentWorkloadTokens:Directory"]);
        using var issuer = new IdentityAccessTokens(configuration);
        Console.WriteLine("Development workload-token provisioner started; token values are not logged.");

        while (!cancellationToken.IsCancellationRequested)
        {
            await WriteTokenAsync(directory, "delivery.jwt",
                issuer.IssueDevelopmentWorkloadToken("delivery-internal", DateTimeOffset.UtcNow), cancellationToken);
            await WriteTokenAsync(directory, "merchant.jwt",
                issuer.IssueDevelopmentWorkloadToken("merchant-internal", DateTimeOffset.UtcNow), cancellationToken);
            Console.WriteLine("Refreshed the local Delivery and Merchant Worker tokens.");
            await Task.Delay(RefreshInterval, cancellationToken);
        }
    }

    private static string ResolvePrivateDirectory(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || !Path.IsPathFullyQualified(configuredPath))
        {
            throw new InvalidOperationException("An explicit absolute Development workload-token directory is required.");
        }

        var directory = Path.GetFullPath(configuredPath);
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException("Create the private Development workload-token directory before provisioning.");
        }

        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Development workload-token directories must not contain linked ancestors.");
            }

            var gitMarker = Path.Combine(current.FullName, ".git");
            if (File.Exists(gitMarker) || Directory.Exists(gitMarker))
            {
                throw new InvalidOperationException("Development workload tokens must be stored outside Git working trees.");
            }
        }

        return directory;
    }

    private static async Task WriteTokenAsync(string directory, string fileName, string token,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine(directory, fileName);
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Development workload-token files must not be links.");
        }

        var temporary = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        var bytes = Encoding.ASCII.GetBytes(token + Environment.NewLine);
        try
        {
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

            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, target, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
