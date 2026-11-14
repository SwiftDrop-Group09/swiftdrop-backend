namespace SwiftDrop.Delivery.Api.Application.Dispatch;

public sealed record WorkloadQueryInput(string? Page = null, string? PageSize = null,
    bool InvalidParameters = false);
public sealed record CourierWorkloadResponse(Guid CourierId, bool Active, bool OnDuty,
    int ActiveCount, int Capacity, IReadOnlyList<Guid> ZoneIds);
public sealed record WorkloadPage(IReadOnlyList<CourierWorkloadResponse> Items,
    int TotalCount, int Page, int PageSize);
public sealed record DispatchAssignmentResponse(Guid ParcelId, string TrackingNumber,
    string Status, DateOnly ServiceDate, DateTimeOffset AssignedAt);
public sealed record DispatchAssignmentsPage(Guid CourierId, IReadOnlyList<DispatchAssignmentResponse> Items,
    int TotalCount, int Page, int PageSize);

public interface IWorkloadReadStore
{
    Task<WorkloadPage> GetWorkloadAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<DispatchAssignmentsPage?> GetAssignmentsAsync(Guid courierId, int page, int pageSize,
        CancellationToken cancellationToken);
}
