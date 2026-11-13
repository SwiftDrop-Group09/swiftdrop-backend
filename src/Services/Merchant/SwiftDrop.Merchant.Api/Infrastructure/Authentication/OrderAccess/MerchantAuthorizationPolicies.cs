namespace SwiftDrop.Merchant.Api.Infrastructure.Authentication.OrderAccess;

/// <summary>
/// Policy names for order endpoints. Use them as
/// <c>.RequireAuthorization(MerchantAuthorizationPolicies.OrdersWrite)</c>.
/// Existing endpoints keep the default policy and remain JWT-only.
/// </summary>
public static class MerchantAuthorizationPolicies
{
    /// <summary>API key with <c>orders:write</c>, approved business. Submitting orders (FR-M04).</summary>
    public const string OrdersWrite = "OrdersWrite";

    /// <summary>Owner JWT, or API key with <c>orders:read</c>; approved business. Reading orders (FR-M07).</summary>
    public const string OrdersRead = "OrdersRead";
}
