using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Admin.Pricing;

public sealed class PricingAdministrationHandler(
    IPricingAdministrationStore store,
    ICurrentDeliveryUser user)
{
    public async Task<PricingResult<PricingOverviewPage>> OverviewAsync(int page, int pageSize, Guid? zoneId, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated) return new(null, PricingFailure.Unauthenticated);
        if (!user.IsAdmin) return new(null, PricingFailure.Forbidden);
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue || zoneId == Guid.Empty)
            return new(null, PricingFailure.InvalidInput);
        return new(await store.ListOverviewAsync(page, pageSize, zoneId, cancellationToken));
    }

    public async Task<PricingResult<PricingPage>> ListAsync(int? page, int? pageSize, Guid? zoneId, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated)
        {
            return new(null, PricingFailure.Unauthenticated);
        }

        if (!user.IsAdmin)
        {
            return new(null, PricingFailure.Forbidden);
        }

        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 20, 1, 100);
        var result = await store.ListPricingAsync(p, size, zoneId, cancellationToken);
        return new(result);
    }

    public async Task<PricingResult<PricingTierDto>> CreateAsync(PricingTierInput? input, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId is not { } actorId)
        {
            return new(null, PricingFailure.Unauthenticated);
        }

        if (!user.IsAdmin)
        {
            return new(null, PricingFailure.Forbidden);
        }

        if (input is null)
        {
            return new(null, PricingFailure.InvalidInput);
        }

        var errors = new Dictionary<string, string[]>();

        if (input.ZoneId == Guid.Empty)
        {
            errors["zoneId"] = ["Valid zoneId is required."];
        }

        if (!Enum.TryParse<ParcelSize>(input.Size, ignoreCase: false, out var parsedSize) || !Enum.IsDefined(parsedSize))
        {
            errors["size"] = ["Size must be Small, Medium, or Large."];
        }

        if (string.IsNullOrWhiteSpace(input.PricingVersion))
        {
            errors["pricingVersion"] = ["Pricing version is required."];
        }

        if (input.BaseRate < 0)
        {
            errors["baseRate"] = ["Base rate must be non-negative."];
        }

        if (input.PerKgRate < 0)
        {
            errors["perKgRate"] = ["Per kg rate must be non-negative."];
        }

        if (input.Currency != "LKR")
        {
            errors["currency"] = ["Only LKR currency is supported."];
        }

        if (errors.Count > 0)
        {
            return new(null, PricingFailure.InvalidInput, errors);
        }

        return await store.CreatePricingTierAsync(input, actorId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<PricingResult<PricingTierDto>> UpdateAsync(Guid id, PricingTierUpdateInput? input, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId is not { } actorId)
        {
            return new(null, PricingFailure.Unauthenticated);
        }

        if (!user.IsAdmin)
        {
            return new(null, PricingFailure.Forbidden);
        }

        if (id == Guid.Empty)
        {
            return new(null, PricingFailure.NotFound);
        }

        if (input is null)
        {
            return new(null, PricingFailure.InvalidInput);
        }

        return await store.UpdatePricingTierAsync(id, input, actorId, DateTimeOffset.UtcNow, cancellationToken);
    }
}
