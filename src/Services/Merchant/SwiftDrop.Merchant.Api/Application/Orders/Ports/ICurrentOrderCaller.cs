namespace SwiftDrop.Merchant.Api.Application.Orders.Ports;

/// <summary>
/// Resolves the business making the current order request, from either the
/// owner's JWT or an API key. Order handlers must take the business id from
/// here and never from the request body (TC-SEC-03).
/// </summary>
public interface ICurrentOrderCaller
{
    /// <summary>Resolved once per request scope; later calls return the same result.</summary>
    Task<OrderCaller> ResolveAsync(CancellationToken cancellationToken);
}
