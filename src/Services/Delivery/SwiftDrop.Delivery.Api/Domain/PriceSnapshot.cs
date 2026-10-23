namespace SwiftDrop.Delivery.Api.Domain;

public sealed class PriceSnapshot
{
    private PriceSnapshot() { }

    public PriceSnapshot(Guid id, Guid parcelId, PricingTier tier, decimal baseFee,
        decimal weightFee, decimal total, DateTimeOffset calculatedAt)
    {
        Id = DomainGuard.Id(id, nameof(id));
        ParcelId = DomainGuard.Id(parcelId, nameof(parcelId));
        PricingTierId = tier.Id;
        ZoneId = tier.ZoneId;
        PricingVersion = tier.PricingVersion;
        Currency = tier.Currency;
        BaseFee = DomainGuard.Money(baseFee, nameof(baseFee));
        WeightFee = DomainGuard.Money(weightFee, nameof(weightFee));
        Total = DomainGuard.Money(total, nameof(total));
        if (BaseFee + WeightFee != Total)
        {
            throw new ArgumentException("The snapshot total must equal its fee components.", nameof(total));
        }

        CalculatedAt = calculatedAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public Guid ParcelId { get; private set; }
    public Guid PricingTierId { get; private set; }
    public Guid ZoneId { get; private set; }
    public decimal BaseFee { get; private set; }
    public decimal WeightFee { get; private set; }
    public string PricingVersion { get; private set; } = null!;
    public string Currency { get; private set; } = null!;
    public decimal Total { get; private set; }
    public DateTimeOffset CalculatedAt { get; private set; }
}
