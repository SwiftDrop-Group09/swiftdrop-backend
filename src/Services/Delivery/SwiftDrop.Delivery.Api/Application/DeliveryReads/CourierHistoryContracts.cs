using SwiftDrop.Delivery.Api.Application.Bookings;

namespace SwiftDrop.Delivery.Api.Application.DeliveryReads;

public sealed record CourierHistoryQueryInput(string? Page = null, string? PageSize = null, bool InvalidParameters = false);
public sealed record DeliveryPageQuery(int Page, int PageSize);
public sealed record CourierHistoryPage(IReadOnlyList<ParcelBookingResponse> Items, int TotalCount, int Page, int PageSize);

public interface ICourierHistoryStore
{
    Task<CourierHistoryPage> GetHistoryAsync(Guid courierId, DeliveryPageQuery query, CancellationToken cancellationToken);
}
