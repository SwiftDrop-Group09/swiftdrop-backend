using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Configuration;

using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.CourierProof;
using SwiftDrop.Delivery.Tests.Courier;

namespace SwiftDrop.Delivery.Tests.ClientHarness;

/// <summary>Explicit opt-in acceptance against a caller-confirmed dedicated test bucket.
/// Never loads user secrets, AWS profiles, default credentials or creates bucket/policies.</summary>
public static class PrivateStorageAcceptance
{
    public static Task<int> RunAsync(TextWriter output, CancellationToken cancellationToken = default) =>
        RunAsync(output, Environment.GetEnvironmentVariable, cancellationToken);

    internal static async Task<int> RunAsync(TextWriter output, Func<string, string?> environment, CancellationToken cancellationToken = default)
    {
        string[] required = ["Endpoint", "Bucket", "Region", "AccessKey", "SecretKey"];
        var missing = required.Where(x => string.IsNullOrWhiteSpace(environment("ProofStorage__" + x)))
            .Select(x => "ProofStorage__" + x).ToArray();
        var confirmed = environment("SWIFTDROP_COURIER_ACCEPTANCE_BUCKET");
        var bucket = environment("ProofStorage__Bucket");
        if (missing.Length != 0 || string.IsNullOrWhiteSpace(confirmed) || !string.Equals(bucket, confirmed, StringComparison.Ordinal))
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                check = "live-private-storage",
                status = "blocked",
                missingSettings = missing,
                dedicatedTestBucketConfirmationRequired = true
            }));
            return 3;
        }
        var settings = required.Append("SessionToken").ToDictionary(x => "ProofStorage:" + x,
            x => environment("ProofStorage__" + x));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var storage = new S3ProofStorage(configuration);
        var key = $"proofs/{Guid.NewGuid():N}/{Guid.NewGuid():N}/photo.png";
        var staged = false;
        var cleanupSucceeded = false;
        var status = "failed";
        try
        {
            // Only this invocation's generated key is ever deleted. Mark it before
            // upload so an ambiguous write outcome remains eligible for cleanup.
            staged = true;
            var decoded = new DecodedProof(ProofType.Photo, "image/png", ProofTestSupport.Image());
            var proof = await storage.StageAsync(key, decoded, cancellationToken);
            await storage.VerifyAsync(proof, cancellationToken);
            await Assert.ThrowsAsync<ProofStorageException>(() => storage.StageAsync(key, decoded, cancellationToken));
            await storage.VerifyAsync(proof, cancellationToken);
            var found = false;
            await foreach (var item in storage.ListAsync(cancellationToken)) if (item.StorageRef == key) found = true;
            Assert.True(found);
            // The production adapter already validates HTTPS (loopback HTTP only)
            // and private ACL/policy, AES256, MIME, length, bytes and hash readback.
            var origin = new Uri(settings["ProofStorage:Endpoint"]!);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            using var anonymous = await http.GetAsync(new Uri(origin, $"/{Uri.EscapeDataString(bucket!)}/{key}"),
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            Assert.True(anonymous.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
            status = "passed";
        }
        catch (Exception)
        {
            // No raw SDK errors, object keys, bucket values or credentials in logs.
            status = "failed";
        }
        finally
        {
            if (staged)
            {
                try { await storage.DeleteAsync(key, CancellationToken.None); cleanupSucceeded = true; }
                catch (Exception) { status = "failed"; }
            }
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            check = "live-private-storage",
            status,
            cleanupSucceeded,
            productionBucketAcceptance = false
        }));
        return status == "passed" ? 0 : 1;
    }
}
