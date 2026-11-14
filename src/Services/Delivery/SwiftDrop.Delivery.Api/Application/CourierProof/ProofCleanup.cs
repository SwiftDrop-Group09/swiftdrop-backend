namespace SwiftDrop.Delivery.Api.Application.CourierProof;

public sealed class ProofCleanup(IPrivateProofStorage storage, IProofCleanupStore store, TimeProvider clock)
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow().AddHours(-24);
        var deleted = 0;
        await foreach (var candidate in storage.ListAsync(cancellationToken))
        {
            if (candidate.LastModified >= cutoff) continue;
            if (await store.DeleteIfUnreferencedAsync(candidate.StorageRef,
                token => storage.DeleteAsync(candidate.StorageRef, token), cancellationToken)) deleted++;
        }
        return deleted;
    }
}
