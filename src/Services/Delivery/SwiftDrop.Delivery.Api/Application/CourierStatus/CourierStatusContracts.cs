using System.Text.Json.Serialization;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.CourierStatus;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CourierStatusInput(string? Status, long? ExpectedVersion, string? Notes = null, string? FailureReason = null);
public sealed record CourierStatusCommand(Guid ParcelId, ParcelStatus Status, long ExpectedVersion,
    string? Notes, CourierFailureReason? FailureReason, string Key, string Fingerprint);
public sealed record CourierStatusValidation(CourierStatusCommand? Command, IReadOnlyDictionary<string, string[]> Errors);
public sealed record StoredStatusResponse(int StatusCode, string Body, string Location);
public enum CourierStatusFailure
{
    Unauthenticated, Forbidden, InvalidInput, NotFound, StaleVersion, InvalidTransition,
    KeyConflict, KeyExpired, OperationInProgress, Unavailable
}
public sealed record CourierStatusResult(StoredStatusResponse? Response, CourierStatusFailure? Failure,
    IReadOnlyDictionary<string, string[]>? Errors = null, long? CurrentVersion = null);
public sealed record CourierStatusState(Parcel Parcel, CourierAssignment Assignment, PriceSnapshot Snapshot);
public sealed record StatusReservation(bool IsNew, IdempotencyRecord Record);

public interface ICourierStatusStore
{
    Task<ICourierStatusTransaction> BeginAsync(CancellationToken cancellationToken);
}

public interface ICourierStatusTransaction : IAsyncDisposable
{
    Task<CourierStatusState?> LoadAuthorizedAsync(Guid courierId, Guid parcelId, CancellationToken cancellationToken);
    Task<StatusReservation> ReserveAsync(Guid courierId, CourierStatusCommand command, DateTimeOffset now, CancellationToken cancellationToken);
    Task SaveAsync(CourierStatusState state, BookingHistory history, OutboxMessage outbox,
        IdempotencyRecord reservation, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
}

public enum CourierStatusStorageFailure { InProgress, Unavailable }
public sealed class CourierStatusStoreException(CourierStatusStorageFailure failure, Exception? inner = null)
    : Exception("Courier status persistence could not complete.", inner)
{
    public CourierStatusStorageFailure Failure { get; } = failure;
}
