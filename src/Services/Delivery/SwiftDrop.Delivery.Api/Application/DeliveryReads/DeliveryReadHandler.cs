using System.Globalization;

namespace SwiftDrop.Delivery.Api.Application.DeliveryReads;

public sealed class DeliveryReadHandler(ICurrentDeliveryUser currentUser, IDeliveryReadStore store, OperationsClock clock)
{
    public async Task<DeliveryReadResult<AssignedParcelPage>> GetAssignmentsAsync(AssignmentQueryInput input,
        CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } courierId || courierId == Guid.Empty)
            return new(null, DeliveryReadFailure.Unauthenticated);
        if (!currentUser.IsCourier) return new(null, DeliveryReadFailure.Forbidden);

        var errors = new Dictionary<string, string[]>();
        if (input.InvalidParameters) errors["query"] = ["Supply only one value for each documented query parameter."];
        DateOnly? date = null;
        if (input.Date is not null)
        {
            if (DateOnly.TryParseExact(input.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) date = parsed;
            else errors["date"] = ["Use a valid YYYY-MM-DD service date."];
        }

        var includeOverdue = true;
        if (input.IncludeOverdue is not null && !bool.TryParse(input.IncludeOverdue, out includeOverdue))
            errors["includeOverdue"] = ["Use true or false."];
        var pagination = DeliveryPagination.Parse(input.Page, input.PageSize, errors);
        if (errors.Count != 0) return new(null, DeliveryReadFailure.InvalidInput, errors);

        try
        {
            return new(await store.GetAssignmentsAsync(courierId,
                new AssignmentQuery(date ?? clock.Today(), includeOverdue, pagination.Page, pagination.PageSize), cancellationToken), null);
        }
        catch (DeliveryReadUnavailableException) { return new(null, DeliveryReadFailure.Unavailable); }
    }

    public async Task<DeliveryReadResult<ParcelDetailResponse>> GetParcelAsync(string parcelId,
        bool invalidParameters = false, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
            return new(null, DeliveryReadFailure.Unauthenticated);
        if (!currentUser.IsCourier && !currentUser.IsCustomer && !currentUser.IsOperations)
            return new(null, DeliveryReadFailure.Forbidden);
        if (invalidParameters) return new(null, DeliveryReadFailure.InvalidInput,
            new Dictionary<string, string[]> { ["query"] = ["This operation has no query parameters."] });
        if (!Guid.TryParse(parcelId, out var id) || id == Guid.Empty) return new(null, DeliveryReadFailure.NotFound);

        try
        {
            var result = await store.GetParcelAsync(new ParcelReadScope(userId,
                currentUser.IsCourier, currentUser.IsCustomer, currentUser.IsOperations), id, cancellationToken);
            return result is null ? new(null, DeliveryReadFailure.NotFound) : new(result, null);
        }
        catch (DeliveryReadUnavailableException) { return new(null, DeliveryReadFailure.Unavailable); }
    }

}
