namespace SwiftDrop.Delivery.Api.Application.DeliveryReads;

public sealed class CourierHistoryDetailHandler(ICurrentDeliveryUser user, ICourierHistoryDetailStore store)
{
    public async Task<DeliveryReadResult<CourierHistoryDetailResponse>> ExecuteAsync(string parcelId,
        bool invalidParameters = false, CancellationToken cancellationToken = default)
    {
        if (!user.IsAuthenticated || user.UserId is not { } actor || actor == Guid.Empty)
            return new(null, DeliveryReadFailure.Unauthenticated);
        if (!user.IsCourier) return new(null, DeliveryReadFailure.Forbidden);
        if (invalidParameters) return new(null, DeliveryReadFailure.InvalidInput,
            new Dictionary<string, string[]> { ["query"] = ["This read has no query parameters."] });
        if (!Guid.TryParse(parcelId, out var id) || id == Guid.Empty) return new(null, DeliveryReadFailure.NotFound);
        try
        {
            var result = await store.GetHistoryDetailAsync(actor, id, cancellationToken);
            return result is null ? new(null, DeliveryReadFailure.NotFound) : new(result, null);
        }
        catch (DeliveryReadUnavailableException) { return new(null, DeliveryReadFailure.Unavailable); }
    }
}
