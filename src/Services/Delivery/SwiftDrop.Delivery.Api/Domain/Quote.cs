namespace SwiftDrop.Delivery.Api.Domain;

public sealed class Quote
{
    private Quote() { }

    public Quote(Guid id, Guid ownerId, string requestFingerprint, string deliveryInput,
        PricingTier pricingTier, decimal total, DateTimeOffset createdAt)
    {
        Id = DomainGuard.Id(id, nameof(id));
        OwnerId = DomainGuard.Id(ownerId, nameof(ownerId));
        RequestFingerprint = DomainGuard.Text(requestFingerprint, nameof(requestFingerprint));
        DeliveryInput = DomainGuard.JsonObject(deliveryInput, nameof(deliveryInput));
        PricingTierId = pricingTier.Id;
        PricingVersion = pricingTier.PricingVersion;
        Currency = pricingTier.Currency;
        Total = DomainGuard.Money(total, nameof(total));
        CreatedAt = createdAt.ToUniversalTime();
        ExpiresAt = CreatedAt.AddMinutes(10);
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string RequestFingerprint { get; private set; } = null!;
    public string DeliveryInput { get; private set; } = null!;
    public Guid PricingTierId { get; private set; }
    public string PricingVersion { get; private set; } = null!;
    public string Currency { get; private set; } = null!;
    public decimal Total { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public Guid? ConsumedByParcelId { get; private set; }

    public void Consume(Parcel parcel, DateTimeOffset now)
    {
        if (parcel.CustomerId != OwnerId)
        {
            throw new InvalidOperationException("The parcel does not belong to the quote owner.");
        }

        if (ConsumedByParcelId is not null || now >= ExpiresAt)
        {
            throw new InvalidOperationException("The quote is consumed or expired.");
        }

        ConsumedByParcelId = parcel.Id;
    }
}
