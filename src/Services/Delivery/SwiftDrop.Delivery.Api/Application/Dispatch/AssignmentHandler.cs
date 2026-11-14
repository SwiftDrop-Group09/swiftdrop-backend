using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Dispatch;

public sealed class AssignmentHandler(ICurrentDeliveryUser user, IAssignmentStore store, OperationsClock operations, TimeProvider clock)
{
    private AssignmentFailure? Access() => !user.IsAuthenticated || user.UserId is null || user.UserId == Guid.Empty
        ? AssignmentFailure.Unauthenticated : !user.IsOperations ? AssignmentFailure.Forbidden : null;

    public async Task<AssignmentResult<AssignmentOptions>> OptionsAsync(string parcelId, AssignmentOptionsInput input, CancellationToken cancellationToken)
    {
        if (Access() is { } access) return new(null, access);
        if (!Guid.TryParse(parcelId, out var id) || id == Guid.Empty) return new(null, AssignmentFailure.NotFound);
        var errors = new Dictionary<string, string[]>();
        if (input.InvalidParameters) errors["query"] = ["Supply only documented parameters, once each."];
        if (input.Reassign is not (null or "true" or "false")) errors["reassign"] = ["Supply true or false."];
        var date = AssignmentValidator.Date(input.ServiceDate, errors);
        var pagination = DeliveryPagination.Parse(input.Page, input.PageSize, errors);
        if (errors.Count != 0) return new(null, AssignmentFailure.InvalidInput, errors);
        try
        {
            var today = operations.Today();
            if (date < today) return PastDate<AssignmentOptions>();
            var response = await store.GetOptionsAsync(id, today, operations.TimeZoneId, date,
                pagination.Page, pagination.PageSize, input.Reassign == "true", cancellationToken);
            return response is null ? new(null, AssignmentFailure.NotFound) : new(response, null);
        }
        catch (AssignmentException exception) { return new(null, exception.Failure); }
        catch (DeliveryReadUnavailableException) { return new(null, AssignmentFailure.Unavailable); }
    }

    public async Task<AssignmentResult<AssignedResponse>> AssignAsync(AssignCourierInput? input, string? key, string correlationId, CancellationToken cancellationToken)
    {
        if (Access() is { } access) return new(null, access);
        var errors = new Dictionary<string, string[]>();
        var command = AssignmentValidator.Validate(input, key, errors);
        if (command is null) return new(null, AssignmentFailure.InvalidInput, errors);
        try
        {
            await using var transaction = await store.BeginAsync(cancellationToken);
            var state = await transaction.LockParcelAsync(command.ParcelId, cancellationToken);
            if (state is null) return new(null, AssignmentFailure.NotFound);
            var now = clock.GetUtcNow().ToUniversalTime();
            now = new(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
            var reservation = await transaction.ReserveAsync(user.UserId!.Value, command, now, cancellationToken);
            var record = reservation.Record;
            if (record.PrincipalId != user.UserId.Value.ToString("D") || record.Operation != AssignmentValidator.Operation || record.Key != command.Key)
                return new(null, AssignmentFailure.Unavailable);
            if (!reservation.IsNew && now >= record.ExpiresAt) return new(null, AssignmentFailure.KeyExpired);
            if (record.RequestFingerprint != command.Fingerprint) return new(null, AssignmentFailure.KeyConflict);
            if (!reservation.IsNew)
                return record.StoredResponse is null && record.ParcelId is null
                    ? new(null, AssignmentFailure.OperationInProgress) : new(AssignmentResponseCodec.Replay(record, command), null);
            if (record.StoredResponse is not null || record.ParcelId is not null) return new(null, AssignmentFailure.Unavailable);
            if (state.Parcel.Version != command.ExpectedVersion) return new(null, AssignmentFailure.StaleVersion, CurrentVersion: state.Parcel.Version);
            if (state.Parcel.Status != ParcelStatus.Created) return new(null, AssignmentFailure.InvalidTransition);
            // Calculate after lock waits; a future date may have become today's date.
            var today = operations.Today();
            var date = command.ServiceDate ?? today;
            if (date < today) return PastDate<AssignedResponse>();
            await transaction.CheckEligibilityAsync(state.Parcel, command.CourierId, date, today, cancellationToken);
            var lockedToday = operations.Today();
            if (lockedToday != today)
            {
                date = command.ServiceDate ?? lockedToday;
                if (date < lockedToday) return PastDate<AssignedResponse>();
                await transaction.CheckEligibilityAsync(state.Parcel, command.CourierId, date, lockedToday, cancellationToken);
            }
            now = clock.GetUtcNow().ToUniversalTime();
            now = new(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
            state.Parcel.AssignCourier(command.ExpectedVersion, now);
            var assignment = new CourierAssignment(Guid.NewGuid(), state.Parcel.Id, command.CourierId, date, now);
            var response = AssignmentResponseCodec.Create(state);
            record.CompleteAssignment(state.Parcel.Id, response.Body);
            await transaction.SaveAsync(state, assignment, BookingHistory.Assigned(Guid.NewGuid(), state.Parcel, user.UserId.Value),
                OutboxMessage.Assigned(Guid.NewGuid(), state.Parcel, correlationId), record, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(response, null);
        }
        catch (AssignmentException exception) { return new(null, exception.Failure, CurrentVersion: exception.CurrentVersion); }
        catch (DeliveryReadUnavailableException) { return new(null, AssignmentFailure.Unavailable); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        { return new(null, AssignmentFailure.Unavailable); }
    }

    private static AssignmentResult<T> PastDate<T>() => new(default, AssignmentFailure.InvalidInput,
        new Dictionary<string, string[]> { ["serviceDate"] = ["Choose today or a future operations service date."] });
}
