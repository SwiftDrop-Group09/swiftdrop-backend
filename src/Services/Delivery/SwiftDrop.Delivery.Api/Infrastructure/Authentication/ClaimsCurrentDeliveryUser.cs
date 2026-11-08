using System.Security.Claims;
using SwiftDrop.Delivery.Api.Application;

namespace SwiftDrop.Delivery.Api.Infrastructure.Authentication;

public sealed class ClaimsCurrentDeliveryUser(IHttpContextAccessor accessor) : ICurrentDeliveryUser
{
    // Keep the existing Customer trust boundary: one authenticated identity and
    // one subject, with roles taken exclusively from that same identity.
    private ClaimsIdentity? Identity => accessor.HttpContext?.User.Identities
        .Where(identity => identity.IsAuthenticated).Take(2).ToArray() is [var identity] ? identity : null;

    public bool IsAuthenticated => Identity is not null;
    public Guid? UserId => Identity?.FindAll("sub").Take(2).ToArray() is [var subject]
        && Guid.TryParse(subject.Value, out var id) && id != Guid.Empty ? id : null;
    public bool IsCourier => HasRole("Courier");
    public bool IsCustomer => HasRole("Customer");
    public bool IsOperations => HasRole("Dispatcher") || HasRole("Admin");

    private bool HasRole(string role) => Identity is { } identity
        && (identity.HasClaim(identity.RoleClaimType, role) || identity.HasClaim("roles", role));
}
