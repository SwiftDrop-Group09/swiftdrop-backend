using Microsoft.AspNetCore.Authorization;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication.ApiKeys;

namespace SwiftDrop.Merchant.Api.Infrastructure.Authentication.OrderAccess;

public enum OrderAccess
{
    Read,
    Write
}

/// <summary>The caller must belong to an approved business and be allowed the requested access.</summary>
public sealed class OrderAccessRequirement(OrderAccess access) : IAuthorizationRequirement
{
    public OrderAccess Access { get; } = access;
}

/// <summary>
/// Evaluates <see cref="OrderAccessRequirement"/> against the freshly resolved
/// <see cref="OrderCaller"/>. Failing here after successful authentication
/// produces 403 (missing scope, unapproved business, wrong credential type).
/// </summary>
/// <remarks>
/// ASP.NET Core constructs every registered authorization handler for every
/// policy evaluation, so this handler takes no database-backed dependencies in
/// its constructor; the caller resolver is fetched only when an order policy runs.
/// </remarks>
public sealed class OrderAccessHandler(IHttpContextAccessor contextAccessor)
    : AuthorizationHandler<OrderAccessRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, OrderAccessRequirement requirement)
    {
        var httpContext = contextAccessor.HttpContext;
        if (httpContext is null || HasMixedCredentials(httpContext.Request))
        {
            // With OrdersRead the JWT could succeed while the key is rejected; never let one credential mask another.
            context.Fail(new AuthorizationFailureReason(this, "Send either an Authorization header or an API key, not both."));
            return;
        }

        var currentCaller = httpContext.RequestServices.GetRequiredService<ICurrentOrderCaller>();
        var caller = await currentCaller.ResolveAsync(httpContext.RequestAborted);

        var allowed = requirement.Access switch
        {
            OrderAccess.Write => caller.CanWriteOrders,
            OrderAccess.Read => caller.CanReadOrders,
            _ => false
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, "The caller may not access merchant orders."));
        }
    }

    private static bool HasMixedCredentials(HttpRequest request) =>
        request.Headers.ContainsKey("Authorization") && request.Headers.ContainsKey(ApiKeyAuthenticationDefaults.HeaderName);
}
