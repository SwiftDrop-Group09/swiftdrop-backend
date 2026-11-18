using SwiftDrop.Merchant.Api.Application.Orders.Dashboard;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication.OrderAccess;

namespace SwiftDrop.Merchant.Api.Presentation;

/// <summary>Merchant dashboard route (FR-M06); the gateway's <c>/api/v1/merchants/**</c> route forwards it.</summary>
public static class DashboardEndpoints
{
    public const string DashboardPath = "/api/v1/merchants/me/dashboard";

    public static void MapDashboardEndpoints(this WebApplication app)
    {
        app.MapGet(DashboardPath, async (HttpContext context, MerchantDashboardHandler handler,
            string? from, string? to, CancellationToken cancellationToken) =>
        {
            var result = await handler.GetAsync(new DashboardQuery(from, to), cancellationToken);
            if (result.Value is not { } dashboard)
            {
                return OrderProblems.Failure(context, result.Failure!.Value, result.Errors);
            }

            // Per-business figures: never cache in shared proxies.
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(dashboard);
        }).RequireAuthorization(MerchantAuthorizationPolicies.Dashboard).WithName("GetMerchantDashboard")
          .Produces<MerchantDashboardResponse>(200)
          .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
    }
}
