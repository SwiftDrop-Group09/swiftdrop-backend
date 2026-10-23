using System.Security.Claims;
using SwiftDrop.Delivery.Api.Application;

namespace SwiftDrop.Delivery.Api.Infrastructure.Authentication;

public sealed class ClaimsCurrentCustomer(IHttpContextAccessor contextAccessor) : ICurrentCustomer
{
    private ClaimsPrincipal? Principal => contextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public bool IsCustomer => IsAuthenticated && (Principal!.IsInRole("Customer")
        || Principal.HasClaim("roles", "Customer"));

    public Guid? CustomerId => IsAuthenticated
        && Guid.TryParse(Principal!.FindFirstValue("sub"), out var id) && id != Guid.Empty ? id : null;
}
