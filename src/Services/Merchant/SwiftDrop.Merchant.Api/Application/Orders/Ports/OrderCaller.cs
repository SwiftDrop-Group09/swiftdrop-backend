using SwiftDrop.Merchant.Api.Domain;

namespace SwiftDrop.Merchant.Api.Application.Orders.Ports;

/// <summary>How the current caller proved who they are.</summary>
public enum OrderCallerKind
{
    /// <summary>No single verified credential.</summary>
    None,

    /// <summary>The merchant owner, signed in with an Identity JWT (web portal).</summary>
    MerchantOwner,

    /// <summary>A merchant system, authenticated with an <c>X-API-Key</c>.</summary>
    ApiKey
}

/// <summary>
/// The business behind the current request, resolved only from a verified
/// credential (never from request input). Business status is read fresh on
/// every request so suspension takes effect immediately (NFR-08).
/// </summary>
public sealed record OrderCaller
{
    private static readonly IReadOnlySet<string> NoScopes = new HashSet<string>(StringComparer.Ordinal);

    public static OrderCaller Anonymous { get; } = new(OrderCallerKind.None, null, null, null, NoScopes);

    private OrderCaller(
        OrderCallerKind kind,
        Guid? businessId,
        Guid? apiKeyId,
        BusinessStatus? businessStatus,
        IReadOnlySet<string> scopes)
    {
        Kind = kind;
        BusinessId = businessId;
        ApiKeyId = apiKeyId;
        BusinessStatus = businessStatus;
        Scopes = scopes;
    }

    public static OrderCaller ForOwner(Guid businessId, BusinessStatus status) =>
        new(OrderCallerKind.MerchantOwner, businessId, null, status, NoScopes);

    public static OrderCaller ForApiKey(Guid businessId, Guid apiKeyId, BusinessStatus status, IEnumerable<string> scopes) =>
        new(OrderCallerKind.ApiKey, businessId, apiKeyId, status, new HashSet<string>(scopes, StringComparer.Ordinal));

    public OrderCallerKind Kind { get; }
    public Guid? BusinessId { get; }
    public Guid? ApiKeyId { get; }
    public BusinessStatus? BusinessStatus { get; }

    /// <summary>API key scopes. Owners have none; their access comes from owning the business.</summary>
    public IReadOnlySet<string> Scopes { get; }

    public bool IsApprovedBusiness => BusinessStatus == Domain.BusinessStatus.Approved;

    public bool HasScope(string scope) => Scopes.Contains(scope);

    /// <summary>Submitting orders is for merchant systems only (FR-M04).</summary>
    public bool CanWriteOrders => IsApprovedBusiness && Kind == OrderCallerKind.ApiKey && HasScope(ApiKeyScopes.OrdersWrite);

    /// <summary>Reading orders is for the owner (portal) or a key holding <c>orders:read</c> (FR-M07).</summary>
    public bool CanReadOrders => IsApprovedBusiness &&
        (Kind == OrderCallerKind.MerchantOwner || (Kind == OrderCallerKind.ApiKey && HasScope(ApiKeyScopes.OrdersRead)));
}
