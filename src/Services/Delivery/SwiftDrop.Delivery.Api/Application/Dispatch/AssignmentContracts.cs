using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Dispatch;

public sealed record AssignCourierInput(string? ParcelId, string? CourierId, long? ExpectedVersion,
    bool Reassign = false, string? ServiceDate = null);
public sealed record AssignCourierCommand(Guid ParcelId, Guid CourierId, long ExpectedVersion,
    DateOnly? ServiceDate, string Key, string Fingerprint);
public sealed record AssignmentOptionsInput(string? ServiceDate = null, string? Page = null,
    string? PageSize = null, bool InvalidParameters = false);
public sealed record AssignmentCandidate(Guid CourierId, bool OnDuty, int ActiveCountForServiceDate, int Capacity);
public sealed record AssignmentOptions(Guid ParcelId, long ParcelVersion, DateOnly BusinessToday,
    string OperationsTimeZone, DateOnly ServiceDate, IReadOnlyList<AssignmentCandidate> Items,
    int TotalCount, int Page, int PageSize);
public sealed record AssignedResponse(int StatusCode, string Body, string Location);
public enum AssignmentFailure
{
    Unauthenticated, Forbidden, InvalidInput, NotFound, StaleVersion, InvalidTransition,
    CourierUnavailable, ZoneMismatch, CapacityExceeded, KeyConflict, KeyExpired, OperationInProgress, Unavailable
}
public sealed record AssignmentResult<T>(T? Response, AssignmentFailure? Failure,
    IReadOnlyDictionary<string, string[]>? Errors = null, long? CurrentVersion = null);
public sealed class AssignmentException(AssignmentFailure failure, long? currentVersion = null, Exception? inner = null)
    : Exception("The assignment operation could not complete.", inner)
{
    public AssignmentFailure Failure { get; } = failure;
    public long? CurrentVersion { get; } = currentVersion;
}
public sealed record AssignmentState(Parcel Parcel, PriceSnapshot Snapshot);
public sealed record AssignmentReservation(bool IsNew, IdempotencyRecord Record);
public interface IAssignmentStore
{
    Task<AssignmentOptions?> GetOptionsAsync(Guid parcelId, DateOnly today, string timeZone, DateOnly serviceDate,
        int page, int pageSize, CancellationToken cancellationToken);
    Task<IAssignmentTransaction> BeginAsync(CancellationToken cancellationToken);
}
public interface IAssignmentTransaction : IAsyncDisposable
{
    Task<AssignmentState?> LockParcelAsync(Guid parcelId, CancellationToken cancellationToken);
    Task<AssignmentReservation> ReserveAsync(Guid actor, AssignCourierCommand command, DateTimeOffset now, CancellationToken cancellationToken);
    Task CheckEligibilityAsync(Parcel parcel, Guid courierId, DateOnly date, DateOnly today, CancellationToken cancellationToken);
    Task SaveAsync(AssignmentState state, CourierAssignment assignment, BookingHistory history,
        OutboxMessage outbox, IdempotencyRecord record, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
}
