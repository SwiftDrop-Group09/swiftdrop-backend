namespace SwiftDrop.Delivery.Api.Domain;

/// <summary>Durable Merchant replay binding; it has no expiry and is committed with the booking.</summary>
public sealed class MerchantBookingIdentity
{
    private MerchantBookingIdentity() { }
    public MerchantBookingIdentity(Guid merchantId, string externalOrderId, string idempotencyKey,
        string fingerprint, Guid parcelId)
    {
        MerchantId = merchantId;
        ExternalOrderId = externalOrderId;
        IdempotencyKey = idempotencyKey;
        Fingerprint = fingerprint;
        ParcelId = parcelId;
    }
    public Guid MerchantId { get; private set; }
    public string ExternalOrderId { get; private set; } = null!;
    public string IdempotencyKey { get; private set; } = null!;
    public string Fingerprint { get; private set; } = null!;
    public Guid ParcelId { get; private set; }
}
