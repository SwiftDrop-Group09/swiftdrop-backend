namespace SwiftDrop.Delivery.Api.Application.Admin.Zones;

public sealed class ZoneAdministrationHandler(
    IZoneAdministrationStore store,
    ICurrentDeliveryUser user)
{
    public async Task<ZoneResult<ZonePage>> ListAsync(int? page, int? pageSize, CancellationToken cancellationToken, ZoneQuery? filters = null)
    {
        if (!user.IsAuthenticated)
        {
            return new(null, ZoneFailure.Unauthenticated);
        }

        if (!user.IsAdmin)
        {
            return new(null, ZoneFailure.Forbidden);
        }

        filters ??= new();
        if (filters.Search?.Length > 200 || filters.City?.Length > 200 || filters.NotCity?.Length > 200
            || filters.Status is not (null or "" or "active" or "inactive"))
            return new(null, ZoneFailure.InvalidInput);
        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 20, 1, 100);
        if ((long)(p - 1) * size > int.MaxValue) return new(null, ZoneFailure.InvalidInput);
        var result = await store.ListZonesAsync(p, size, cancellationToken, filters);
        return new(result);
    }

    public async Task<ZoneResult<ZoneDto>> CreateAsync(ZoneInput? input, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId is not { } actorId)
        {
            return new(null, ZoneFailure.Unauthenticated);
        }

        if (!user.IsAdmin)
        {
            return new(null, ZoneFailure.Forbidden);
        }

        if (input is null || string.IsNullOrWhiteSpace(input.Name))
        {
            return new(null, ZoneFailure.InvalidInput,
                new Dictionary<string, string[]> { ["name"] = ["Zone name is required."] });
        }

        return await store.CreateZoneAsync(input, actorId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<ZoneResult<ZoneDto>> UpdateAsync(Guid id, ZoneInput? input, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId is not { } actorId)
        {
            return new(null, ZoneFailure.Unauthenticated);
        }

        if (!user.IsAdmin)
        {
            return new(null, ZoneFailure.Forbidden);
        }

        if (id == Guid.Empty)
        {
            return new(null, ZoneFailure.NotFound);
        }

        if (input is null || string.IsNullOrWhiteSpace(input.Name))
        {
            return new(null, ZoneFailure.InvalidInput,
                new Dictionary<string, string[]> { ["name"] = ["Zone name is required."] });
        }

        return await store.UpdateZoneAsync(id, input, actorId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<ZoneResult<ZoneSummaryResponse>> GetSummaryAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated)
        {
            return new(null, ZoneFailure.Unauthenticated);
        }

        if (!user.IsAdmin && !user.IsOperations)
        {
            return new(null, ZoneFailure.Forbidden);
        }

        var result = await store.GetZoneSummaryAsync(from, to, cancellationToken);
        return new(result);
    }
}
