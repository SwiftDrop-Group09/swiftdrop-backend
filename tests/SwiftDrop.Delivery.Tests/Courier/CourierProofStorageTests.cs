using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.CourierProof;

namespace SwiftDrop.Delivery.Tests.Courier;

public sealed class CourierProofStorageTests
{
    private static string Key => $"proofs/{Guid.NewGuid():N}/{Guid.NewGuid():N}/photo.png";

    [Fact]
    public async Task PrivateEncryptedConditionalCreateIsConfirmedByActualBytesAndCanBeVerifiedAgain()
    {
        using var client = new StorageClient();
        using var storage = new S3ProofStorage(client, "private-proofs");
        var bytes = ProofTestSupport.Image();
        var reference = Key;
        var stored = await storage.StageAsync(reference, new(ProofType.Photo, "image/png", bytes), default);
        Assert.Equal(reference, stored.StorageRef);
        Assert.Equal(bytes.Length, stored.Length);
        Assert.Equal("*", client.IfNoneMatch);
        Assert.Equal(ServerSideEncryptionMethod.AES256, client.Encryption);
        Assert.NotEmpty(client.Checksum!);
        Assert.Equal(1, client.PutCalls);
        Assert.Equal(1, client.GetCalls);
        await storage.VerifyAsync(stored, default);
        Assert.Equal(2, client.GetCalls);
        Assert.Equal(2, client.PrivateChecks);
        await Assert.ThrowsAsync<ProofStorageException>(() => storage.StageAsync(reference, new(ProofType.Photo, "image/png", bytes), default));
        Assert.Equal(bytes, client.Bytes);
    }

    [Theory]
    [InlineData("public-acl")]
    [InlineData("public-policy")]
    [InlineData("unreadable-policy")]
    [InlineData("sdk-client")]
    [InlineData("timeout")]
    [InlineData("unencrypted")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("wrong-type")]
    [InlineData("wrong-size")]
    public async Task UnverifiedPrivacyEncryptionOrObjectIntegrityFailsClosed(string failure)
    {
        using var client = new StorageClient { Failure = failure };
        using var storage = new S3ProofStorage(client, "private-proofs");
        await Assert.ThrowsAsync<ProofStorageException>(() => storage.StageAsync(Key,
            new(ProofType.Photo, "image/png", ProofTestSupport.Image()), default));
        if (failure is "public-acl" or "public-policy" or "unreadable-policy" or "sdk-client" or "timeout") Assert.Equal(0, client.PutCalls);
    }

    [Theory]
    [InlineData("https://public.invalid/proof.jpg")]
    [InlineData("../another-bucket/object")]
    [InlineData("proofs/../../secret")]
    [InlineData("proofs/client-selected-key.png")]
    public async Task OnlyGeneratedParcelBoundKeysCanReachStorage(string reference)
    {
        using var client = new StorageClient();
        using var storage = new S3ProofStorage(client, "private-proofs");
        await Assert.ThrowsAsync<ProofStorageException>(() => storage.StageAsync(reference,
            new(ProofType.Photo, "image/png", ProofTestSupport.Image()), default));
        Assert.Equal(0, client.PutCalls);
        Assert.Equal(0, client.PrivateChecks);
    }

    [Fact]
    public async Task CallerCancellationIsPropagatedRatherThanReportedAsAStorageOutage()
    {
        using var client = new StorageClient { Failure = "timeout" };
        using var storage = new S3ProofStorage(client, "private-proofs");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.StageAsync(Key,
            new(ProofType.Photo, "image/png", ProofTestSupport.Image()), cancellation.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://storage.example.invalid:9000")]
    [InlineData("https://user:password@storage.invalid")]
    [InlineData("https://storage.invalid/path?secret=leak")]
    public async Task MissingOrUnsafeConfigurationDoesNotFallBackToFilesystemOrAmbientAwsCredentials(string? endpoint)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProofStorage:Endpoint"] = endpoint, ["ProofStorage:Bucket"] = "private-proofs", ["ProofStorage:AccessKey"] = "fictional",
            ["ProofStorage:SecretKey"] = "fictional-secret", ["ProofStorage:Region"] = "us-east-1"
        }).Build();
        using var storage = new S3ProofStorage(configuration);
        await Assert.ThrowsAsync<ProofStorageException>(() => storage.StageAsync(Key,
            new(ProofType.Photo, "image/png", ProofTestSupport.Image()), default));
    }

    private sealed class StorageClient() : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1" })
    {
        public string? Failure { get; init; }
        public int PutCalls;
        public int GetCalls;
        public int PrivateChecks;
        public string? IfNoneMatch;
        public string? Checksum;
        public ServerSideEncryptionMethod? Encryption;
        public byte[]? Bytes;

        public override Task<GetBucketAclResponse> GetBucketAclAsync(GetBucketAclRequest request, CancellationToken cancellationToken = default)
        {
            PrivateChecks++;
            if (Failure == "sdk-client") throw new AmazonClientException("private SDK failure");
            if (Failure == "timeout") throw new OperationCanceledException(cancellationToken);
            return Task.FromResult(new GetBucketAclResponse
            {
                Owner = new Owner { Id = "delivery-owner" },
                Grants = [new S3Grant
                {
                    Grantee = Failure == "public-acl" ? new S3Grantee { URI = "http://acs.amazonaws.com/groups/global/AllUsers" }
                        : new S3Grantee { CanonicalUser = "delivery-owner" }, Permission = S3Permission.FULL_CONTROL
                }]
            });
        }

        public override Task<GetBucketPolicyResponse> GetBucketPolicyAsync(string bucketName, CancellationToken cancellationToken = default)
        {
            if (Failure == "unreadable-policy") throw new AmazonS3Exception("private policy failure") { StatusCode = HttpStatusCode.Forbidden };
            if (Failure == "public-policy") return Task.FromResult(new GetBucketPolicyResponse
                { Policy = "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":\"s3:GetObject\"}]}" });
            throw new AmazonS3Exception("No policy") { ErrorCode = "NoSuchBucketPolicy", StatusCode = HttpStatusCode.NotFound };
        }

        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            PutCalls++;
            IfNoneMatch = request.IfNoneMatch;
            Encryption = request.ServerSideEncryptionMethod;
            Checksum = request.ChecksumSHA256;
            Assert.Null(request.CannedACL);
            if (Bytes is not null) throw new AmazonS3Exception("conditional create") { StatusCode = HttpStatusCode.PreconditionFailed };
            using var buffer = new MemoryStream();
            await request.InputStream.CopyToAsync(buffer, cancellationToken);
            Bytes = buffer.ToArray();
            return new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK };
        }

        public override Task<GetObjectResponse> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            if (Failure == "missing") throw new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound };
            var bytes = Failure == "corrupt" ? "different bytes"u8.ToArray() : Bytes!;
            var response = new GetObjectResponse
            {
                ResponseStream = new MemoryStream(bytes, false), ContentLength = Failure == "wrong-size" ? bytes.Length + 1 : bytes.Length,
                LastModified = new DateTime(2026, 10, 28, 12, 10, 0, DateTimeKind.Utc),
                ServerSideEncryptionMethod = Failure == "unencrypted" ? null : ServerSideEncryptionMethod.AES256
            };
            response.Headers.ContentType = Failure == "wrong-type" ? "text/html" : "image/png";
            return Task.FromResult(response);
        }
    }
}
