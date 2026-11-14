namespace SwiftDrop.Delivery.Api.Application.Admin.Pricing;

public interface IPricingAdministrationStore
{
    Task<PricingOverviewPage> ListOverviewAsync(int page, int pageSize, Guid? zoneId, CancellationToken cancellationToken);
    Task<PricingPage> ListPricingAsync(int page, int pageSize, Guid? zoneId, CancellationToken cancellationToken);
    Task<PricingResult<PricingTierDto>> CreatePricingTierAsync(PricingTierInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<PricingResult<PricingTierDto>> UpdatePricingTierAsync(Guid id, PricingTierUpdateInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken);
}
