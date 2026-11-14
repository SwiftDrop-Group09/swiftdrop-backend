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

public enum DeliveryReadFailure { Unauthenticated, Forbidden, InvalidInput, NotFound, Conflict, Unavailable }
public sealed record DeliveryReadResult<T>(T? Response, DeliveryReadFailure? Failure,
    IReadOnlyDictionary<string, string[]>? Errors = null) where T : class;

public interface IDeliveryReadStore
{
    Task<AssignedParcelPage> GetAssignmentsAsync(Guid courierId, AssignmentQuery query, CancellationToken cancellationToken);
    Task<ParcelDetailResponse?> GetParcelAsync(ParcelReadScope scope, Guid parcelId, CancellationToken cancellationToken);
}

public sealed record CustomerParcelQueryInput(string? Status = null, string? From = null, string? To = null,
    string? Search = null, string? Page = null, string? PageSize = null, bool InvalidParameters = false,
    string? ZoneId = null);
public sealed record CustomerParcelQuery(Domain.ParcelStatus? Status, DateTimeOffset? From, DateTimeOffset? To,
    string? Search, int Page, int PageSize, Guid? ZoneId = null);

public sealed record CustomerParcelListItem(Guid Id, string TrackingNumber, string Status, long Version,
    BookingPriceSnapshotResponse PriceSnapshot, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string PickupCity, string DeliveryCity, string RecipientName,
    ReadPackageDetails PackageDetails, bool HasProof);
public sealed record CustomerParcelPage(IReadOnlyList<CustomerParcelListItem> Items,
    int TotalCount, int Page, int PageSize);

public sealed record PublicTimelineEntry(string Status, DateTimeOffset Timestamp, string Label);
public sealed record PublicTrackingSummary(string TrackingNumber, string Status, string OriginCity,
    string DestinationCity, DateTimeOffset LastUpdatedAt, IReadOnlyList<PublicTimelineEntry> Timeline);

public sealed record ParcelEventSnapshot(Guid Id, string Status, long Version, DateTimeOffset LastUpdatedAt);
public sealed record ParcelEventsResponse(Guid Id, string TrackingNumber, string Status, long Version,
    DateTimeOffset LastUpdatedAt, IReadOnlyList<PrivateHistoryResponse> Events);

public sealed record ParcelReceiptResponse(string ReceiptNumber, Guid ParcelId, string TrackingNumber, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt,
    NormalizedAddress PickupAddress, NormalizedAddress DeliveryAddress, NormalizedRecipient Recipient,
    ReadPackageDetails PackageDetails, decimal BaseFee, decimal WeightFee, decimal Total,
    string Currency, string PricingVersion, DateTimeOffset CalculatedAt);
public sealed record ParcelReceiptLookup(Domain.ParcelStatus Status, ParcelReceiptResponse? Receipt);

public sealed record ProofOfDeliveryItem(Guid Id, string Type, string RecipientName, DateTimeOffset UploadedAt, string StorageRef);
public sealed record ProofOfDeliveryResponse(Guid ParcelId, string TrackingNumber, string ProofType,
    string RecipientName, DateTimeOffset UploadedAt, bool HasImage);
public sealed record ParcelProofLookup(Domain.ParcelStatus Status, string TrackingNumber, IReadOnlyList<ProofOfDeliveryItem> Proofs);

public interface ICustomerDeliveryReadStore
{
    Task<CustomerParcelPage> ListParcelsAsync(ParcelReadScope scope, CustomerParcelQuery query, CancellationToken cancellationToken);
    Task<PublicTrackingSummary?> GetPublicTrackingAsync(string trackingNumber, CancellationToken cancellationToken);
    Task<ParcelEventsResponse?> GetParcelEventsAsync(ParcelReadScope scope, Guid parcelId, CancellationToken cancellationToken);
    Task<ParcelEventSnapshot?> GetParcelSnapshotAsync(ParcelReadScope scope, Guid parcelId, CancellationToken cancellationToken);
    Task<ParcelReceiptLookup?> GetReceiptAsync(ParcelReadScope scope, Guid parcelId, CancellationToken cancellationToken);
    Task<ParcelProofLookup?> GetProofAsync(ParcelReadScope scope, Guid parcelId, CancellationToken cancellationToken);
}

public interface IPrivateProofReader
{
    Task<(byte[] Content, string ContentType)?> ReadObjectAsync(string storageRef, CancellationToken cancellationToken);
}

public interface IReceiptPdfRenderer
{
    byte[] Render(ParcelReceiptResponse receipt);
}

public sealed class DeliveryReadUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
