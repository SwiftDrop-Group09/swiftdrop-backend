using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using SwiftDrop.Delivery.Api.Application.CourierProof;

namespace SwiftDrop.Delivery.Api.Infrastructure.CourierProof;

public sealed class S3ProofStorage : IPrivateProofStorage, IDisposable
{
    private readonly Lazy<StorageClient> _storage;

    public S3ProofStorage(IConfiguration configuration) => _storage = new(() => Create(configuration));
    public S3ProofStorage(IAmazonS3 client, string bucket) => _storage = new(() => new(client, bucket, false));

    public Task<StoredProof> StageAsync(string storageRef, DecodedProof proof, CancellationToken cancellationToken) => Protect(async token =>
    {
        RequireKey(storageRef);
        await RequirePrivateBucketAsync(token);
        var state = _storage.Value;
        var hash = SHA256.HashData(proof.Content);
        using var content = new MemoryStream(proof.Content, false);
        await state.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = state.Bucket, Key = storageRef, InputStream = content, ContentType = proof.ContentType,
            IfNoneMatch = "*", ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
            ChecksumSHA256 = Convert.ToBase64String(hash), AutoCloseStream = false
        }, token);
        using var stored = await state.Client.GetObjectAsync(state.Bucket, storageRef, token);
        var result = new StoredProof(storageRef, proof.ContentType, Convert.ToHexStringLower(hash), proof.Content.Length,
            stored.LastModified is { } modified ? new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc))
                : throw new ProofStorageException());
        await VerifyResponseAsync(stored, result, token);
        return result;
    }, cancellationToken);

    public Task VerifyAsync(StoredProof proof, CancellationToken cancellationToken) => Protect(async token =>
    {
        RequireKey(proof.StorageRef);
        await RequirePrivateBucketAsync(token);
        var state = _storage.Value;
        using var stored = await state.Client.GetObjectAsync(state.Bucket, proof.StorageRef, token);
        await VerifyResponseAsync(stored, proof, token);
        return true;
    }, cancellationToken);

    public async IAsyncEnumerable<StagedProofObject> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Protect(async token => { await RequirePrivateBucketAsync(token); return true; }, cancellationToken);
        string? continuation = null;
        do
        {
            var page = await Protect(token => _storage.Value.Client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _storage.Value.Bucket, Prefix = "proofs/", ContinuationToken = continuation, MaxKeys = 100
            }, token), cancellationToken);
            foreach (var item in page.S3Objects ?? [])
            {
                if (item.LastModified is not { } modified || !ProofObjectKeys.IsValid(item.Key)) continue;
                yield return new(item.Key, new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc)));
            }
            continuation = page.IsTruncated == true
                ? page.NextContinuationToken ?? throw new ProofStorageException() : null;
        } while (continuation is not null);
    }

    public Task DeleteAsync(string storageRef, CancellationToken cancellationToken) => Protect(async token =>
    {
        RequireKey(storageRef);
        await RequirePrivateBucketAsync(token);
        var state = _storage.Value;
        // Unique conditional-create keys are never replaced. In versioned buckets,
        // remove the actual unreferenced version rather than only a delete marker.
        try
        {
            var metadata = await state.Client.GetObjectMetadataAsync(state.Bucket, storageRef, token);
            await state.Client.DeleteObjectAsync(new DeleteObjectRequest
            { BucketName = state.Bucket, Key = storageRef, VersionId = metadata.VersionId }, token);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound) { }
        return true;
    }, cancellationToken);

    private async Task RequirePrivateBucketAsync(CancellationToken cancellationToken)
    {
        var state = _storage.Value;
        var acl = await state.Client.GetBucketAclAsync(new GetBucketAclRequest { BucketName = state.Bucket }, cancellationToken);
        var owner = acl.Owner?.Id;
        if (string.IsNullOrWhiteSpace(owner) || acl.Grants is not { } grants
            || grants.Any(x => x.Grantee?.CanonicalUser != owner || !string.IsNullOrEmpty(x.Grantee.URI)
                || !string.IsNullOrEmpty(x.Grantee.EmailAddress))) throw new ProofStorageException();
        try
        {
            var policy = await state.Client.GetBucketPolicyAsync(state.Bucket, cancellationToken);
            using var document = JsonDocument.Parse(policy.Policy);
            if (!document.RootElement.TryGetProperty("Statement", out var statements)) throw new ProofStorageException();
            // Delivery uses identity policy (or the private bucket owner credential).
            // Any resource-policy Allow requires a separate security review; deny-only
            // transport policies are safe. Unsupported/unreadable ACL/policy fails closed.
            var entries = statements.ValueKind == JsonValueKind.Array ? statements.EnumerateArray().ToArray() : [statements];
            if (entries.Any(x => !x.TryGetProperty("Effect", out var effect) || effect.GetString() != "Deny"))
                throw new ProofStorageException();
        }
        catch (AmazonS3Exception exception) when (exception.ErrorCode == "NoSuchBucketPolicy") { }
    }

    private static async Task VerifyResponseAsync(GetObjectResponse stored, StoredProof expected, CancellationToken cancellationToken)
    {
        if (stored.ServerSideEncryptionMethod != ServerSideEncryptionMethod.AES256 || stored.Headers.ContentType != expected.ContentType
            || stored.ContentLength != expected.Length || expected.Length is < 1 or > 64 * 1024 * 1024)
            throw new ProofStorageException();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = await stored.ResponseStream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            length += read;
            if (length > expected.Length) throw new ProofStorageException();
            hash.AppendData(buffer, 0, read);
        }
        if (length != expected.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != expected.Sha256)
            throw new ProofStorageException();
    }

    private static StorageClient Create(IConfiguration configuration)
    {
        var section = configuration.GetSection("ProofStorage");
        var bucket = section["Bucket"];
        var access = section["AccessKey"];
        var secret = section["SecretKey"];
        var region = section["Region"];
        if (string.IsNullOrWhiteSpace(bucket) || bucket.Length is < 3 or > 63
            || bucket.Any(x => !(char.IsAsciiLetterOrDigit(x) || x is '-' or '.'))
            || string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(region)
            || !Uri.TryCreate(section["Endpoint"], UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback))
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.AbsolutePath != "/") throw new ProofStorageException();
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint.AbsoluteUri, AuthenticationRegion = region, ForcePathStyle = true,
            MaxErrorRetry = 0, Timeout = TimeSpan.FromSeconds(30), HttpClientFactory = new NoRedirectHttpClientFactory()
        };
        AWSCredentials credentials = string.IsNullOrWhiteSpace(section["SessionToken"])
            ? new BasicAWSCredentials(access, secret) : new SessionAWSCredentials(access, secret, section["SessionToken"]);
        return new(new AmazonS3Client(credentials, config), bucket, true);
    }

    private static void RequireKey(string reference)
    {
        if (!ProofObjectKeys.IsValid(reference)) throw new ProofStorageException();
    }

    private static async Task<T> Protect<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await action(deadline.Token); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested) { throw new ProofStorageException(exception); }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException or HttpRequestException or IOException or JsonException
            or InvalidOperationException or ArgumentException) { throw new ProofStorageException(exception); }
    }

    public void Dispose()
    {
        if (_storage.IsValueCreated && _storage.Value.Owned) _storage.Value.Client.Dispose();
    }

    private sealed record StorageClient(IAmazonS3 Client, string Bucket, bool Owned);
    private sealed class NoRedirectHttpClientFactory : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) =>
            new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }
}
