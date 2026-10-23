namespace SwiftDrop.Delivery.Api.Domain;

public sealed class PricingTier
{
    private PricingTier() { }

    public PricingTier(Guid id, Guid zoneId, ParcelSize size, string pricingVersion,
        decimal baseRate, decimal perKgRate, string currency = "LKR")
    {
        if (!Enum.IsDefined(size))
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        Id = DomainGuard.Id(id, nameof(id));
        ZoneId = DomainGuard.Id(zoneId, nameof(zoneId));
        Size = size;
        PricingVersion = DomainGuard.Text(pricingVersion, nameof(pricingVersion));
        BaseRate = DomainGuard.Money(baseRate, nameof(baseRate));
        PerKgRate = DomainGuard.Money(perKgRate, nameof(perKgRate));
        Currency = DomainGuard.Currency(currency);
        IsActive = true;
    }

    public Guid Id { get; private set; }
    public Guid ZoneId { get; private set; }
    public ParcelSize Size { get; private set; }
    public string PricingVersion { get; private set; } = null!;
    public decimal BaseRate { get; private set; }
    public decimal PerKgRate { get; private set; }
    public string Currency { get; private set; } = null!;
    public bool IsActive { get; private set; }
}
