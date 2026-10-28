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
        var query = DeliveryPagination.Parse(input.Page, input.PageSize, errors);
        if (errors.Count != 0) return new(null, DeliveryReadFailure.InvalidInput, errors);
        try { return new(await store.GetHistoryAsync(actor, query, cancellationToken), null); }
        catch (DeliveryReadUnavailableException) { return new(null, DeliveryReadFailure.Unavailable); }
    }
}
