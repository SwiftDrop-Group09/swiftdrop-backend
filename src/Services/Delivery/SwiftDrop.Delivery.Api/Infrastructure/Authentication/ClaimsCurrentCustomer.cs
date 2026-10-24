using System.Security.Claims;

using SwiftDrop.Delivery.Api.Application;

namespace SwiftDrop.Delivery.Api.Infrastructure.Authentication;

public sealed class ClaimsCurrentCustomer(IHttpContextAccessor contextAccessor) : ICurrentCustomer
{
    // The entry adapter must establish one authenticated identity. Never borrow
    // a subject or role from another identity attached to the same principal.
    private ClaimsIdentity? Identity => contextAccessor.HttpContext?.User.Identities
        .Where(candidate => candidate.IsAuthenticated).Take(2).ToArray() is [var identity] ? identity : null;

    public bool IsAuthenticated => Identity is not null;

    public bool IsCustomer => Identity is { } identity && (identity.HasClaim(identity.RoleClaimType, "Customer")
        || identity.HasClaim("roles", "Customer"));

    public Guid? CustomerId => Identity?.FindAll("sub").Take(2).ToArray() is [var subject]
        && Guid.TryParse(subject.Value, out var id) && id != Guid.Empty ? id : null;
}
