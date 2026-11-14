using SwiftDrop.Delivery.Api.Application.CourierStatus;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.CourierProof;

public sealed record ProofFile(ProofType Type, string ContentType, byte[] Content);
public sealed record ProofInput(string? RecipientName, long? ExpectedVersion, IReadOnlyList<ProofFile> Files);
public sealed record ProofCommand(Guid ParcelId, string RecipientName, long ExpectedVersion,
    string Key, string Fingerprint, IReadOnlyList<ProofFile> Files);
public sealed record DecodedProof(ProofType Type, string ContentType, byte[] Content);
public sealed record StoredProof(string StorageRef, string ContentType, string Sha256, long Length, DateTimeOffset UploadedAt);
public sealed record StagedProofObject(string StorageRef, DateTimeOffset LastModified);

public interface IProofImageValidator
{
    Task<DecodedProof> ValidateAsync(ProofFile file, CancellationToken cancellationToken);
}

// No URL, caller-selected key, public upload, or public download capability.
public interface IPrivateProofStorage
{
    Task<StoredProof> StageAsync(string storageRef, DecodedProof proof, CancellationToken cancellationToken);
    Task VerifyAsync(StoredProof proof, CancellationToken cancellationToken);
    IAsyncEnumerable<StagedProofObject> ListAsync(CancellationToken cancellationToken);
    Task DeleteAsync(string storageRef, CancellationToken cancellationToken);
}

public interface ICourierProofStore
{
    Task<ICourierProofTransaction> BeginProofAsync(CancellationToken cancellationToken);
}

public interface ICourierProofTransaction : IAsyncDisposable
{
    Task<CourierStatusState?> LoadAuthorizedAsync(Guid courierId, Guid parcelId, CancellationToken cancellationToken);
    Task<StatusReservation> ReserveProofAsync(Guid courierId, ProofCommand command, DateTimeOffset now, CancellationToken cancellationToken);
    Task LockObjectsAsync(IEnumerable<string> storageRefs, CancellationToken cancellationToken);
    Task SaveProofAsync(CourierStatusState state, IReadOnlyList<ProofMetadata> proofs, BookingHistory history,
        OutboxMessage outbox, IdempotencyRecord reservation, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IProofCleanupStore
{
    Task<bool> DeleteIfUnreferencedAsync(string storageRef, Func<CancellationToken, Task> delete, CancellationToken cancellationToken);
}

public sealed class InvalidProofException : Exception;
public sealed class ProofStorageException(Exception? inner = null) : Exception("Private proof storage is unavailable.", inner);
