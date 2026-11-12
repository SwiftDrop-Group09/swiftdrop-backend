using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.CourierProof;

/// <summary>
/// Stores private courier proof images as bytea rows in the swiftdrop_delivery
/// database (Neon PostgreSQL). No external object storage service is required.
/// </summary>
public sealed class PostgresProofStorage(Func<DeliveryDbContext> contextFactory) : IPrivateProofStorage
{
    public async Task<StoredProof> StageAsync(string storageRef, DecodedProof proof,
        CancellationToken cancellationToken)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(proof.Content));
        var now = DateTimeOffset.UtcNow;

        await using var db = contextFactory();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO proof_objects (storage_ref, content_type, sha256, length, data, stored_at)
            VALUES ({0}, {1}, {2}, {3}, {4}, {5})
            ON CONFLICT (storage_ref) DO NOTHING
            """,
            [storageRef, proof.ContentType, sha256, proof.Content.LongLength, proof.Content, now],
            cancellationToken);

        // Re-read to get the canonical stored_at (handles ON CONFLICT no-op case).
        var row = await db.Database
            .SqlQueryRaw<ProofRow>(
                "SELECT sha256, length, stored_at FROM proof_objects WHERE storage_ref = {0}",
                storageRef)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ProofStorageException();

        if (row.Sha256 != sha256 || row.Length != proof.Content.LongLength)
            throw new ProofStorageException();

        return new StoredProof(storageRef, proof.ContentType, row.Sha256, row.Length, row.StoredAt);
    }

    public async Task VerifyAsync(StoredProof proof, CancellationToken cancellationToken)
    {
        await using var db = contextFactory();
        var row = await db.Database
            .SqlQueryRaw<ProofVerifyRow>(
                "SELECT sha256, length, data FROM proof_objects WHERE storage_ref = {0}",
                proof.StorageRef)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ProofStorageException();

        var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(row.Data));
        if (actualSha256 != proof.Sha256 || row.Length != proof.Length)
            throw new ProofStorageException();
    }

    public async IAsyncEnumerable<StagedProofObject> ListAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var db = contextFactory();
        var rows = await db.Database
            .SqlQueryRaw<ProofListRow>(
                "SELECT storage_ref, stored_at FROM proof_objects ORDER BY stored_at")
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
            yield return new StagedProofObject(row.StorageRef, row.StoredAt);
    }

    public async Task DeleteAsync(string storageRef, CancellationToken cancellationToken)
    {
        await using var db = contextFactory();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM proof_objects WHERE storage_ref = {0}",
            [storageRef],
            cancellationToken);
    }

    // EF Core raw SQL projection types — internal to this class.
    private sealed record ProofRow(string Sha256, long Length, DateTimeOffset StoredAt);
    private sealed record ProofVerifyRow(string Sha256, long Length, byte[] Data);
    private sealed record ProofListRow(string StorageRef, DateTimeOffset StoredAt);
}

