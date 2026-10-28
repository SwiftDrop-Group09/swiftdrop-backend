using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Tests.Courier;

internal static class ProofTestSupport
{
    public static byte[] Image(bool jpeg = false, byte shade = 100)
    {
        using var bitmap = new SKBitmap(8, 6);
        bitmap.Erase(new SKColor(shade, 40, 200));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    public static MultipartFormDataContent Form(long version = 5, bool photo = true, bool signature = false,
        byte[]? photoBytes = null, string name = "Private recipient", bool jpeg = false)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(name), "recipientName");
        form.Add(new StringContent(version.ToString(System.Globalization.CultureInfo.InvariantCulture)), "expectedVersion");
        if (photo) AddFile(form, "photo", photoBytes ?? Image(jpeg), jpeg ? "image/jpeg" : "image/png");
        if (signature) AddFile(form, "signature", Image(), "image/png");
        return form;
    }

    public static void AddFile(MultipartFormDataContent form, string field, byte[] bytes, string type)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(content, field, "../../untrusted-filename.exe");
    }

    public static async Task<HttpResponseMessage> Send(HttpClient client, Guid parcelId, HttpContent content, string? key = "proof-test-key")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/deliveries/{parcelId:D}/pod") { Content = content };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    public static void UseStorage(IServiceCollection services, MemoryProofStorage storage)
    {
        services.RemoveAll<IPrivateProofStorage>();
        services.AddSingleton<IPrivateProofStorage>(storage);
    }

    public static async Task<string> Signature(StatusTestScenario scenario)
    {
        await using var context = scenario.Context();
        return JsonSerializer.Serialize(new
        {
            graph = (await scenario.GraphAsync()).Signature(),
            proofs = await context.ProofMetadata.AsNoTracking().Where(x => x.ParcelId == scenario.ParcelId).OrderBy(x => x.Id).ToArrayAsync(),
            keys = await context.IdempotencyRecords.AsNoTracking().Where(x => x.PrincipalId == scenario.CourierId.ToString("D")
                && x.Operation == ProofValidator.Operation).OrderBy(x => x.Id).ToArrayAsync()
        });
    }
}

internal sealed class MemoryProofStorage : IPrivateProofStorage
{
    public ConcurrentDictionary<string, (StoredProof Proof, byte[] Bytes)> Objects { get; } = new();
    public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 12, 10, 0, TimeSpan.Zero);
    public Func<StoredProof, CancellationToken, Task>? AfterStage { get; set; }
    public Func<StoredProof, CancellationToken, Task>? BeforeVerify { get; set; }
    public bool FailStage { get; set; }
    public int FailAtStage { get; set; }
    public bool FailVerify { get; set; }
    public bool Expired { get; set; }
    public bool WrongReference { get; set; }
    public int Stages;
    public int Verifies;

    public async Task<StoredProof> StageAsync(string storageRef, DecodedProof proof, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref Stages);
        if (FailStage || count == FailAtStage) throw new ProofStorageException();
        var stored = new StoredProof(storageRef, proof.ContentType, Convert.ToHexStringLower(SHA256.HashData(proof.Content)),
            proof.Content.Length, Expired ? Now.AddDays(-2) : Now);
        if (!Objects.TryAdd(storageRef, (stored, proof.Content.ToArray()))) throw new ProofStorageException();
        if (AfterStage is not null) await AfterStage(stored, cancellationToken);
        return WrongReference ? stored with { StorageRef = "proofs/unrelated-object" } : stored;
    }

    public async Task VerifyAsync(StoredProof proof, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Verifies);
        if (BeforeVerify is not null) await BeforeVerify(proof, cancellationToken);
        if (FailVerify || !Objects.TryGetValue(proof.StorageRef, out var stored)
            || Convert.ToHexStringLower(SHA256.HashData(stored.Bytes)) != proof.Sha256) throw new ProofStorageException();
    }

    public async IAsyncEnumerable<StagedProofObject> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var entry in Objects.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(entry.Proof.StorageRef, entry.Proof.UploadedAt);
        }
    }

    public Task DeleteAsync(string storageRef, CancellationToken cancellationToken)
    {
        Objects.TryRemove(storageRef, out _);
        return Task.CompletedTask;
    }
}
