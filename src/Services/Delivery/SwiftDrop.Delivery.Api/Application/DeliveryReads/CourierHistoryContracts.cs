using SwiftDrop.Delivery.Api.Application.Bookings;

namespace SwiftDrop.Delivery.Api.Application.DeliveryReads;

public sealed record CourierHistoryQueryInput(string? Page = null, string? PageSize = null, bool InvalidParameters = false,
    string? Status = null, string? Date = null, string? Search = null);
public sealed record DeliveryPageQuery(int Page, int PageSize, Domain.ParcelStatus? Status = null,
    DateOnly? Date = null, string? Search = null);
public sealed record CourierHistoryPage(IReadOnlyList<ParcelBookingResponse> Items, int TotalCount, int Page, int PageSize);

public interface ICourierHistoryStore
{
    Task<CourierHistoryPage> GetHistoryAsync(Guid courierId, DeliveryPageQuery query, CancellationToken cancellationToken);
}

public sealed record CourierHistoryDetailResponse(Guid Id, string TrackingNumber, string Status, long Version,
    BookingPriceSnapshotResponse PriceSnapshot, DateTimeOffset CreatedAt, string PickupCity, string DeliveryCity,
    ReadPackageDetails PackageDetails, DateOnly ServiceDate, DateTimeOffset AssignedAt,
    DateTimeOffset? PickedUpAt, DateTimeOffset CompletedAt, string? FailureReason, bool HasProof);

public interface ICourierHistoryDetailStore
{
    Task<CourierHistoryDetailResponse?> GetHistoryDetailAsync(Guid courierId, Guid parcelId, CancellationToken cancellationToken);
}
