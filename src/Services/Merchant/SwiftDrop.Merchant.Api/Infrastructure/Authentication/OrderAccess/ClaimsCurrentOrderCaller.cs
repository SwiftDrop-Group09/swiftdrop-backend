using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication.ApiKeys;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Api.Infrastructure.Authentication.OrderAccess;

/// <summary>
/// Turns the authenticated principal into an <see cref="OrderCaller"/>:
/// an API key identity resolves through its <c>business_id</c> claim, an owner
/// JWT through <c>businesses.user_id</c>. Anything else — no identity, several
/// identities, unparsable ids — resolves to <see cref="OrderCaller.Anonymous"/>.
/// </summary>
public sealed class ClaimsCurrentOrderCaller(
    IHttpContextAccessor contextAccessor,
    MerchantDbContext database) : ICurrentOrderCaller
{
    private OrderCaller? _resolved;

    public async Task<OrderCaller> ResolveAsync(CancellationToken cancellationToken) =>
        _resolved ??= await ResolveCoreAsync(cancellationToken);

    private async Task<OrderCaller> ResolveCoreAsync(CancellationToken cancellationToken)
    {
        // Exactly one verified identity; never combine claims from different credentials.
        var identities = contextAccessor.HttpContext?.User.Identities.Where(identity => identity.IsAuthenticated).Take(2).ToArray();
        if (identities is not [var identity])
        {
            return OrderCaller.Anonymous;
        }

        return IsApiKey(identity)
            ? await ResolveApiKeyAsync(identity, cancellationToken)
            : await ResolveOwnerAsync(identity, cancellationToken);
    }

    private static bool IsApiKey(ClaimsIdentity identity) =>
        identity.HasClaim(ApiKeyClaimTypes.AuthenticationMethod, ApiKeyAuthenticationDefaults.AuthenticationMethod);

    private async Task<OrderCaller> ResolveApiKeyAsync(ClaimsIdentity identity, CancellationToken cancellationToken)
    {
        // An API key identity must never also claim to be a person.
        if (identity.HasClaim(claim => claim.Type == "sub")
            || SingleGuid(identity, ApiKeyClaimTypes.BusinessId) is not { } businessId
            || SingleGuid(identity, ApiKeyClaimTypes.ApiKeyId) is not { } apiKeyId)
        {
            return OrderCaller.Anonymous;
        }

        var status = await database.Businesses.AsNoTracking()
            .Where(business => business.Id == businessId)
            .Select(business => (Domain.BusinessStatus?)business.Status)
            .FirstOrDefaultAsync(cancellationToken);

        var scopes = identity.FindAll(ApiKeyClaimTypes.Scope).Select(claim => claim.Value);
        return status is { } current
            ? OrderCaller.ForApiKey(businessId, apiKeyId, current, scopes)
            : OrderCaller.Anonymous;
    }

    private async Task<OrderCaller> ResolveOwnerAsync(ClaimsIdentity identity, CancellationToken cancellationToken)
    {
        if (SingleGuid(identity, "sub") is not { } userId)
        {
            return OrderCaller.Anonymous;
        }

        var business = await database.Businesses.AsNoTracking()
            .Where(candidate => candidate.UserId == userId)
            .Select(candidate => new { candidate.Id, candidate.Status })
            .FirstOrDefaultAsync(cancellationToken);

        return business is null
            ? OrderCaller.Anonymous
            : OrderCaller.ForOwner(business.Id, business.Status);
    }

    private static Guid? SingleGuid(ClaimsIdentity identity, string claimType) =>
        identity.FindAll(claimType).Take(2).ToArray() is [var claim]
        && Guid.TryParse(claim.Value, out var value) && value != Guid.Empty
            ? value
            : null;
}
