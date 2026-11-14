using SwiftDrop.Delivery.Api.Application.DeliveryReads;

namespace SwiftDrop.Delivery.Api.Application.Dispatch;

public sealed class CourierWorkloadHandler(ICurrentDeliveryUser currentUser, IWorkloadReadStore store)
{
    public Task<DeliveryReadResult<WorkloadPage>> GetWorkloadAsync(WorkloadQueryInput input,
        CancellationToken cancellationToken = default) =>
        ReadAsync<WorkloadPage>(input, async (page, size) => (WorkloadPage?)await store.GetWorkloadAsync(page, size, cancellationToken));

    public Task<DeliveryReadResult<DispatchAssignmentsPage>> GetAssignmentsAsync(string courierId,
        WorkloadQueryInput input, CancellationToken cancellationToken = default) =>
        ReadAsync(input, async (page, size) =>
        {
            if (!Guid.TryParse(courierId, out var id) || id == Guid.Empty) return null;
            return await store.GetAssignmentsAsync(id, page, size, cancellationToken);
        });

    private async Task<DeliveryReadResult<T>> ReadAsync<T>(WorkloadQueryInput input,
        Func<int, int, Task<T?>> read) where T : class
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } actor || actor == Guid.Empty)
            return new(null, DeliveryReadFailure.Unauthenticated);
        if (!currentUser.IsOperations) return new(null, DeliveryReadFailure.Forbidden);
        var errors = new Dictionary<string, string[]>();
        if (input.InvalidParameters) errors["query"] = ["Supply only documented query parameters, with one value each."];
        var pagination = DeliveryPagination.Parse(input.Page, input.PageSize, errors);
        if (errors.Count != 0) return new(null, DeliveryReadFailure.InvalidInput, errors);
        try
        {
            var response = await read(pagination.Page, pagination.PageSize);
            return response is null ? new(null, DeliveryReadFailure.NotFound) : new(response, null);
        }
        catch (DeliveryReadUnavailableException) { return new(null, DeliveryReadFailure.Unavailable); }
    }
}
