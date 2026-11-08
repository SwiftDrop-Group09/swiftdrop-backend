using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.Quotes;

namespace SwiftDrop.Delivery.Api.Application.DeliveryReads;

public sealed record AssignmentQueryInput(string? Date = null, string? IncludeOverdue = null,
    string? Page = null, string? PageSize = null, bool InvalidParameters = false);
public sealed record AssignmentQuery(DateOnly Date, bool IncludeOverdue, int Page, int PageSize);
public sealed record ParcelReadScope(Guid UserId, bool IsCourier, bool IsCustomer, bool IsOperations);
public sealed record ReadPackageDetails(decimal WeightKg, string Size);

public sealed record AssignedParcelResponse(Guid Id, string TrackingNumber, string Status, long Version,
    BookingPriceSnapshotResponse PriceSnapshot, DateTimeOffset CreatedAt,
    NormalizedAddress PickupAddress, NormalizedAddress DeliveryAddress,
    ReadPackageDetails PackageDetails, NormalizedRecipient Recipient, DateOnly ServiceDate, bool IsOverdue);
public sealed record AssignedParcelPage(IReadOnlyList<AssignedParcelResponse> Items,
    int TotalCount, int Page, int PageSize);

public sealed record CurrentAssignmentResponse(string Status, DateOnly ServiceDate,
    DateTimeOffset AssignedAt, DateTimeOffset? PickedUpAt, DateTimeOffset? DeliveredAt);
public sealed record PrivateHistoryResponse(string? FromStatus, string ToStatus,
    string? ChangeReason, string? DeliveryNote, DateTimeOffset Timestamp);
public sealed record ParcelDetailResponse(Guid Id, string TrackingNumber, string Status, long Version,
    BookingPriceSnapshotResponse PriceSnapshot, DateTimeOffset CreatedAt,
    NormalizedAddress PickupAddress, NormalizedAddress DeliveryAddress,
    ReadPackageDetails PackageDetails, NormalizedRecipient Recipient,
    CurrentAssignmentResponse? Assignment, IReadOnlyList<PrivateHistoryResponse> History);

public enum DeliveryReadFailure { Unauthenticated, Forbidden, InvalidInput, NotFound, Unavailable }
public sealed record DeliveryReadResult<T>(T? Response, DeliveryReadFailure? Failure,
    IReadOnlyDictionary<string, string[]>? Errors = null) where T : class;

public interface IDeliveryReadStore
{
    Task<AssignedParcelPage> GetAssignmentsAsync(Guid courierId, AssignmentQuery query, CancellationToken cancellationToken);
    Task<ParcelDetailResponse?> GetParcelAsync(ParcelReadScope scope, Guid parcelId, CancellationToken cancellationToken);
}

public sealed class DeliveryReadUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
