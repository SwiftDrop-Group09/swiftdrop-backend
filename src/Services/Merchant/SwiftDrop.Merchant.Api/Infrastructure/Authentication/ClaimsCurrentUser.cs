using System.Security.Claims;

using SwiftDrop.Merchant.Api.Application;

namespace SwiftDrop.Merchant.Api.Infrastructure.Authentication;

public sealed class ClaimsCurrentUser(IHttpContextAccessor contextAccessor) : ICurrentUser
{
    // The entry adapter must establish one authenticated identity. Never borrow
    // a subject or role from another identity attached to the same principal.
    private ClaimsIdentity? Identity => contextAccessor.HttpContext?.User.Identities
        .Where(candidate => candidate.IsAuthenticated).Take(2).ToArray() is [var identity] ? identity : null;

    public bool IsAuthenticated => Identity is not null;

    public bool IsAdmin => Identity is { } identity && (identity.HasClaim(identity.RoleClaimType, "Admin")
        || identity.HasClaim("roles", "Admin"));

    public bool IsMerchant => Identity is { } identity && (identity.HasClaim(identity.RoleClaimType, "Merchant")
        || identity.HasClaim("roles", "Merchant"));

    public Guid? UserId => Identity?.FindAll("sub").Take(2).ToArray() is [var subject]
        && Guid.TryParse(subject.Value, out var id) && id != Guid.Empty ? id : null;
}

