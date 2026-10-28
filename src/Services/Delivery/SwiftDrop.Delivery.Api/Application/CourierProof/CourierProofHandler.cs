using SwiftDrop.Delivery.Api.Application.CourierStatus;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.CourierProof;

public sealed class CourierProofHandler(ICurrentDeliveryUser user, ICourierProofStore store,
    IProofImageValidator images, IPrivateProofStorage storage, TimeProvider clock)
{
    public async Task<CourierStatusResult> ExecuteAsync(string parcelId, ProofInput input, string? key,
        string correlationId, CancellationToken cancellationToken = default)
    {
        if (!user.IsAuthenticated || user.UserId is not { } actor || actor == Guid.Empty)
            return new(null, CourierStatusFailure.Unauthenticated);
        if (!user.IsCourier) return new(null, CourierStatusFailure.Forbidden);
        if (!Guid.TryParse(parcelId, out var id) || id == Guid.Empty) return new(null, CourierStatusFailure.NotFound);
        if (ProofValidator.Validate(id, input, key) is not { } command) return new(null, CourierStatusFailure.InvalidInput);
        try
        {
            // Short, rolled-back preflight avoids decoding/uploading for an unauthorized,
            // stale, invalid or already completed operation. No reservation is left behind.
            await using (var preflight = await store.BeginProofAsync(cancellationToken))
            {
                var checkedRequest = await CheckAsync(preflight, actor, command, cancellationToken);
                if (checkedRequest.Result is not null) return checkedRequest.Result;
                CheckVersionAndState(checkedRequest.State!.Parcel, command);
            }

            var upload = Guid.NewGuid().ToString("N");
            var staged = new List<StoredProof>();
            // Validate every file before any upload; input fingerprints use original
            // bytes, while storage receives only freshly encoded, metadata-free pixels.
            var decoded = new List<DecodedProof>();
            foreach (var file in command.Files.OrderBy(x => x.Type))
            {
                var validated = await images.ValidateAsync(file, cancellationToken);
                if (validated.Type != file.Type || validated.ContentType != file.ContentType) throw new ProofStorageException();
                decoded.Add(validated);
            }
            foreach (var file in decoded)
            {
                var suffix = file.ContentType == "image/png" ? "png" : "jpg";
                var reference = $"proofs/{id:N}/{upload}/{file.Type.ToString().ToLowerInvariant()}.{suffix}";
                var stored = await storage.StageAsync(reference, file, cancellationToken);
                if (stored.StorageRef != reference || stored.ContentType != file.ContentType || stored.Length != file.Content.Length
                    || stored.Sha256 != Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file.Content))
                    || stored.UploadedAt.Offset != TimeSpan.Zero) throw new ProofStorageException();
                staged.Add(stored);
            }

            await using var transaction = await store.BeginProofAsync(cancellationToken);
            var final = await CheckAsync(transaction, actor, command, cancellationToken);
            if (final.Result is not null) return final.Result;
            var state = final.State!;
            CheckVersionAndState(state.Parcel, command);
            await transaction.LockObjectsAsync(staged.Select(x => x.StorageRef), cancellationToken);
            foreach (var proof in staged) await storage.VerifyAsync(proof, cancellationToken);
            var now = UtcNow();
            if (staged.Any(x => x.UploadedAt <= now.AddHours(-24))) return new(null, CourierStatusFailure.InvalidInput);
            var metadata = staged.Select((proof, index) => new ProofMetadata(Guid.NewGuid(), id, decoded[index].Type,
                proof.StorageRef, command.RecipientName, actor, proof.UploadedAt)).ToArray();
            state.Parcel.CompleteWithProof(command.ExpectedVersion, metadata, now);
            state.Assignment.MarkDelivered(now);
            var response = StatusResponseCodec.Create(state.Parcel, state.Snapshot);
            var record = final.Reservation!.Record;
            record.CompleteDeliveryWithProof(id, response.Body);
            await transaction.SaveProofAsync(state, metadata, BookingHistory.ProofCompleted(Guid.NewGuid(), state.Parcel, actor),
                OutboxMessage.ProofCompleted(Guid.NewGuid(), state.Parcel, correlationId), record, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(response, null);
        }
        catch (InvalidProofException) { return new(null, CourierStatusFailure.InvalidInput); }
        catch (CourierTransitionException exception)
        {
            return exception.Failure == CourierTransitionFailure.StaleVersion
                ? new(null, CourierStatusFailure.StaleVersion, CurrentVersion: exception.CurrentVersion)
                : new(null, CourierStatusFailure.InvalidTransition);
        }
        catch (CourierStatusStoreException exception)
        {
            return new(null, exception.Failure == CourierStatusStorageFailure.InProgress
                ? CourierStatusFailure.OperationInProgress : CourierStatusFailure.Unavailable);
        }
        catch (Exception exception) when (exception is ProofStorageException or InvalidOperationException or ArgumentException or OverflowException)
        { return new(null, CourierStatusFailure.Unavailable); }
        // Never eagerly delete after an uncertain commit. Reference-aware daily cleanup
        // resolves all unreferenced uploads, including cancellation or response loss.
    }

    private async Task<CheckedRequest> CheckAsync(ICourierProofTransaction transaction, Guid actor, ProofCommand command,
        CancellationToken cancellationToken)
    {
        var state = await transaction.LoadAuthorizedAsync(actor, command.ParcelId, cancellationToken);
        if (state is null || state.Parcel.Id != command.ParcelId || state.Assignment.ParcelId != command.ParcelId
            || state.Assignment.CourierId != actor || (state.Assignment.Status != AssignmentStatus.Active
                && !(state.Parcel.Status == ParcelStatus.Delivered && state.Assignment.Status == AssignmentStatus.Delivered)))
            return new(null, null, new(null, CourierStatusFailure.NotFound));
        if (state.Snapshot.ParcelId != command.ParcelId) return new(null, null, new(null, CourierStatusFailure.Unavailable));
        var reservation = await transaction.ReserveProofAsync(actor, command, UtcNow(), cancellationToken);
        var record = reservation.Record;
        if (record.PrincipalId != actor.ToString("D") || record.Operation != ProofValidator.Operation || record.Key != command.Key)
            return new(null, null, new(null, CourierStatusFailure.Unavailable));
        if (!reservation.IsNew && UtcNow() >= record.ExpiresAt) return new(null, null, new(null, CourierStatusFailure.KeyExpired));
        if (record.RequestFingerprint != command.Fingerprint) return new(null, null, new(null, CourierStatusFailure.KeyConflict));
        if (!reservation.IsNew)
        {
            if (record.StoredResponse is null && record.ParcelId is null)
                return new(null, null, new(null, CourierStatusFailure.OperationInProgress));
            var statusCommand = new CourierStatusCommand(command.ParcelId, ParcelStatus.Delivered, command.ExpectedVersion,
                null, null, command.Key, command.Fingerprint);
            return new(null, null, StatusResponseCodec.TryRead(record, statusCommand, out var replay)
                ? new(replay, null) : new(null, CourierStatusFailure.Unavailable));
        }
        if (record.StoredResponse is not null || record.ParcelId is not null)
            return new(null, null, new(null, CourierStatusFailure.Unavailable));
        return new(state, reservation, null);
    }

    private static void CheckVersionAndState(Parcel parcel, ProofCommand command)
    {
        if (parcel.Version != command.ExpectedVersion)
            throw new CourierTransitionException(CourierTransitionFailure.StaleVersion, parcel.Version);
        if (parcel.Status != ParcelStatus.OutForDelivery)
            throw new CourierTransitionException(CourierTransitionFailure.InvalidTransition, parcel.Version);
    }

    private DateTimeOffset UtcNow()
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        return new(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }

    private sealed record CheckedRequest(CourierStatusState? State, StatusReservation? Reservation, CourierStatusResult? Result);
}
