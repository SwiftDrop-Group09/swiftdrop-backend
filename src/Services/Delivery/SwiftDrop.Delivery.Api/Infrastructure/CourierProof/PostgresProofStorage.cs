using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.CourierProof;

/// <summary>
/// Stores private courier proof images as bytea rows in the <c>swiftdrop_delivery</c>
/// database (Neon PostgreSQL), eliminating external object storage dependencies.
/// </summary>
public sealed class PostgresProofStorage : IPrivateProofStorage
{
    private readonly Func<DeliveryDbContext> _contextFactory;

    /// <summary>
    /// Initializes a new <see cref="PostgresProofStorage"/> instance.
    /// </summary>
    /// <param name="contextFactory">Factory that creates a scoped <see cref="DeliveryDbContext"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="contextFactory"/> is null.</exception>
    public PostgresProofStorage(Func<DeliveryDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Stages a validated proof payload idempotently in <c>proof_objects</c> and verifies its digest.
    /// </summary>
    /// <param name="storageRef">Validated proof object key.</param>
    /// <param name="proof">Decoded proof image payload and content type.</param>
    /// <param name="cancellationToken">Token to cancel the database operation.</param>
    /// <returns>The stored proof metadata including SHA-256 digest, length, and upload timestamp.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="proof"/> is null.</exception>
    /// <exception cref="ProofStorageException">Thrown when the key is invalid, storage fails, or stored metadata mismatches.</exception>
    public Task<StoredProof> StageAsync(string storageRef, DecodedProof proof,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        RequireValidKey(storageRef);

        return ProtectAsync(async token =>
        {
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(proof.Content));
            var now = DateTimeOffset.UtcNow;

            await using var db = _contextFactory();
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO proof_objects (storage_ref, content_type, sha256, length, data, stored_at)
                VALUES ({0}, {1}, {2}, {3}, {4}, {5})
                ON CONFLICT (storage_ref) DO NOTHING
                """,
                [storageRef, proof.ContentType, sha256, proof.Content.LongLength, proof.Content, now],
                token);

            // Re-read canonical stored_at so idempotent ON CONFLICT no-ops still return consistent metadata.
            var row = await db.Database
                .SqlQueryRaw<ProofRow>(
                    "SELECT sha256, length, stored_at FROM proof_objects WHERE storage_ref = {0}",
                    storageRef)
                .FirstOrDefaultAsync(token)
                ?? throw new ProofStorageException();

            if (row.Sha256 != sha256 || row.Length != proof.Content.LongLength)
                throw new ProofStorageException();

            return new StoredProof(storageRef, proof.ContentType, row.Sha256, row.Length, row.StoredAt);
        }, cancellationToken);
    }

    /// <summary>
    /// Verifies that a previously staged proof exists and matches its expected SHA-256 digest and byte length.
    /// </summary>
    /// <param name="proof">Expected stored proof metadata.</param>
    /// <param name="cancellationToken">Token to cancel the database operation.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="proof"/> is null.</exception>
    /// <exception cref="ProofStorageException">Thrown when the proof is missing, corrupted, or unreachable.</exception>
    public Task VerifyAsync(StoredProof proof, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        RequireValidKey(proof.StorageRef);

        return ProtectAsync(async token =>
        {
            await using var db = _contextFactory();
            var row = await db.Database
                .SqlQueryRaw<ProofVerifyRow>(
                    "SELECT length, data FROM proof_objects WHERE storage_ref = {0}",
                    proof.StorageRef)
                .FirstOrDefaultAsync(token)
                ?? throw new ProofStorageException();

            var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(row.Data));
            if (actualSha256 != proof.Sha256 || row.Length != proof.Length)
                throw new ProofStorageException();

            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Lists all staged proof object keys ordered by upload timestamp without loading binary payloads.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the database operation.</param>
    /// <returns>An asynchronous stream of staged proof object references.</returns>
    /// <exception cref="ProofStorageException">Thrown when the database query fails.</exception>
    public async IAsyncEnumerable<StagedProofObject> ListAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rows = await ProtectAsync(async token =>
        {
            await using var db = _contextFactory();
            return await db.Database
                .SqlQueryRaw<ProofListRow>(
                    "SELECT storage_ref, stored_at FROM proof_objects ORDER BY stored_at")
                .ToListAsync(token);
        }, cancellationToken);

        foreach (var row in rows)
            yield return new StagedProofObject(row.StorageRef, row.StoredAt);
    }

    /// <summary>
    /// Deletes a staged or orphaned proof object by its storage reference.
    /// </summary>
    /// <param name="storageRef">Validated proof object key to remove.</param>
    /// <param name="cancellationToken">Token to cancel the database operation.</param>
    /// <exception cref="ProofStorageException">Thrown when the key is invalid or deletion fails.</exception>
    public Task DeleteAsync(string storageRef, CancellationToken cancellationToken)
    {
        RequireValidKey(storageRef);

        return ProtectAsync(async token =>
        {
            await using var db = _contextFactory();
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM proof_objects WHERE storage_ref = {0}",
                [storageRef],
                token);
            return true;
        }, cancellationToken);
    }

    private static void RequireValidKey(string storageRef)
    {
        if (!ProofObjectKeys.IsValid(storageRef))
            throw new ProofStorageException();
    }

    private static async Task<T> ProtectAsync<T>(Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken);
        }
        catch (ProofStorageException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            throw new ProofStorageException(exception);
        }
    }

    private sealed record ProofRow(string Sha256, long Length, DateTimeOffset StoredAt);
    private sealed record ProofVerifyRow(long Length, byte[] Data);
    private sealed record ProofListRow(string StorageRef, DateTimeOffset StoredAt);
}
