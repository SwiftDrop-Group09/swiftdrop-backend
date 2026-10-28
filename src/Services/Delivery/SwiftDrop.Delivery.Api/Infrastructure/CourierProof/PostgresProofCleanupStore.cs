using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.CourierProof;

public sealed class PostgresProofCleanupStore(Func<DeliveryDbContext> contextFactory) : IProofCleanupStore
{
    public async Task<bool> DeleteIfUnreferencedAsync(string storageRef, Func<CancellationToken, Task> delete, CancellationToken cancellationToken)
    {
        var database = contextFactory();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // The same per-object lock is held shared by completion until commit. If it
        // is busy, leave this candidate for the next daily pass; never wait/delete.
        var acquired = await database.Database.SqlQuery<bool>(
            $"SELECT pg_try_advisory_xact_lock({ProofObjectKeys.LockId(storageRef)}) AS \"Value\"").SingleAsync(cancellationToken);
        if (!acquired || await database.ProofMetadata.AsNoTracking().AnyAsync(x => x.StorageRef == storageRef, cancellationToken)) return false;
        await delete(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
