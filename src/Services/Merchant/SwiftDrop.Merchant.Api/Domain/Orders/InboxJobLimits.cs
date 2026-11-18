namespace SwiftDrop.Merchant.Api.Domain.Orders;

/// <summary>
/// Single source of truth for inbox job field sizes. The domain guards and the
/// EF Core mapping both read these values, so the API, entity and database
/// column definitions cannot drift apart.
/// </summary>
public static class InboxJobLimits
{
    public const int ExternalOrderIdMaxLength = 128;
    public const int IdempotencyKeyMaxLength = 128;
    public const int FingerprintLength = 64;
    public const int OrderPayloadMaxLength = 16_384;
    public const int StateMaxLength = 32;
    public const int ErrorCodeMaxLength = 64;
    public const int TrackingNumberMaxLength = 64;
    public const int ParcelStatusMaxLength = 32;
    public const int PricingVersionMaxLength = 64;
    public const int CurrencyLength = 3;
    public const int TotalPrecision = 18;
    public const int TotalScale = 2;
}
