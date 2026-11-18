namespace SwiftDrop.Delivery.Api.Application.DeliveryReads;

public sealed class CourierHistoryHandler(ICurrentDeliveryUser user, ICourierHistoryStore store)
{
    public async Task<DeliveryReadResult<CourierHistoryPage>> ExecuteAsync(CourierHistoryQueryInput input,
        CancellationToken cancellationToken = default)
    {
        if (!user.IsAuthenticated || user.UserId is not { } actor || actor == Guid.Empty)
            return new(null, DeliveryReadFailure.Unauthenticated);
        if (!user.IsCourier) return new(null, DeliveryReadFailure.Forbidden);
        var errors = new Dictionary<string, string[]>();
        if (input.InvalidParameters) errors["query"] = ["Supply only one value for each documented query parameter."];
        var pagination = DeliveryPagination.Parse(input.Page, input.PageSize, errors);
        var status = CourierReadValidation.Status(input.Status, true, errors);
        var date = CourierReadValidation.Date(input.Date, errors);
        var search = CourierReadValidation.Search(input.Search, errors);
        var query = new DeliveryPageQuery(pagination.Page, pagination.PageSize, status, date, search);
        if (errors.Count != 0) return new(null, DeliveryReadFailure.InvalidInput, errors);
        try { return new(await store.GetHistoryAsync(actor, query, cancellationToken), null); }
        catch (DeliveryReadUnavailableException) { return new(null, DeliveryReadFailure.Unavailable); }
    }
}
