using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.CourierStatus;

public sealed class CourierStatusHandler(ICurrentDeliveryUser currentUser, ICourierStatusStore store, TimeProvider clock)
{
    public async Task<CourierStatusResult> ExecuteAsync(string parcelId, CourierStatusInput? input, string? key,
        string correlationId, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } actor || actor == Guid.Empty)
            return new(null, CourierStatusFailure.Unauthenticated);
        if (!currentUser.IsCourier) return new(null, CourierStatusFailure.Forbidden);
        if (!Guid.TryParse(parcelId, out var id) || id == Guid.Empty) return new(null, CourierStatusFailure.NotFound);
        var validation = CourierStatusValidator.Validate(id, input, key);
        if (validation.Command is not { } command) return new(null, CourierStatusFailure.InvalidInput, validation.Errors);

        try
        {
            await using var transaction = await store.BeginAsync(cancellationToken);
            var state = await transaction.LoadAuthorizedAsync(actor, id, cancellationToken);
            if (state is null || state.Parcel.Id != id || state.Assignment.ParcelId != id || state.Assignment.CourierId != actor
                || (state.Assignment.Status != AssignmentStatus.Active
                    && !(state.Parcel.Status == ParcelStatus.Failed && state.Assignment.Status == AssignmentStatus.Failed)
                    && !(state.Parcel.Status == ParcelStatus.Delivered && state.Assignment.Status == AssignmentStatus.Delivered)))
                return new(null, CourierStatusFailure.NotFound);
            if (state.Snapshot.ParcelId != id) return new(null, CourierStatusFailure.Unavailable);
            var reservation = await transaction.ReserveAsync(actor, command, UtcNow(), cancellationToken);
            var record = reservation.Record;
            if (record.PrincipalId != actor.ToString("D") || record.Operation != CourierStatusValidator.Operation || record.Key != command.Key)
                return new(null, CourierStatusFailure.Unavailable);
            if (!reservation.IsNew && UtcNow() >= record.ExpiresAt) return new(null, CourierStatusFailure.KeyExpired);
            if (record.RequestFingerprint != command.Fingerprint) return new(null, CourierStatusFailure.KeyConflict);
            if (!reservation.IsNew)
            {
                if (record.StoredResponse is null && record.ParcelId is null) return new(null, CourierStatusFailure.OperationInProgress);
                return StatusResponseCodec.TryRead(record, command, out var replay)
                    ? new(replay, null) : new(null, CourierStatusFailure.Unavailable);
            }

            if (record.StoredResponse is not null || record.ParcelId is not null) return new(null, CourierStatusFailure.Unavailable);
            var previous = state.Parcel.Status;
            CourierTransitionPolicy.ValidateMetadata(command.Status, command.Notes, command.FailureReason);
            var now = UtcNow();
            state.Parcel.AdvanceCourierStatus(command.Status, command.ExpectedVersion, now);
            if (command.Status == ParcelStatus.PickedUp) state.Assignment.MarkPickedUp(now);
            if (command.Status == ParcelStatus.Failed) state.Assignment.MarkFailed();
            var history = BookingHistory.CourierChanged(Guid.NewGuid(), state.Parcel, previous, actor, command.Notes, command.FailureReason);
            var outbox = OutboxMessage.CourierChanged(Guid.NewGuid(), state.Parcel, previous, correlationId);
            var response = StatusResponseCodec.Create(state.Parcel, state.Snapshot);
            record.CompleteStatusUpdate(id, response.Body);
            await transaction.SaveAsync(state, history, outbox, record, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(response, null);
        }
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
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException)
        {
            // Inconsistent operational data/clock or corrupt stored records must not partially commit.
            return new(null, CourierStatusFailure.Unavailable);
        }
    }

    private DateTimeOffset UtcNow()
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        return new(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }
}
