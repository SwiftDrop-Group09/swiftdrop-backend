using SwiftDrop.Delivery.Api.Application.Admin.Pricing;

namespace SwiftDrop.Delivery.Api.Presentation;

public sealed class AdminPricingEndpointMetadata;

public static class AdminPricingEndpoints
{
    public static void MapAdminPricing(this WebApplication app)
    {
        app.MapGet("/api/v1/pricing-tiers/overview", async (HttpContext context, PricingAdministrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            var allowed = new[] { "page", "pageSize", "zoneId" };
            if (query.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal)) || query.Any(pair => pair.Value.Count != 1))
                return AdminProblems.PricingFailureResult(context, PricingFailure.InvalidInput);
            var page = 1; var pageSize = 20; Guid? zoneId = null;
            if ((query.ContainsKey("page") && !int.TryParse(query["page"], out page))
                || (query.ContainsKey("pageSize") && !int.TryParse(query["pageSize"], out pageSize)))
                return AdminProblems.PricingFailureResult(context, PricingFailure.InvalidInput);
            if (query.ContainsKey("zoneId"))
            {
                if (!Guid.TryParse(query["zoneId"], out var parsed)) return AdminProblems.PricingFailureResult(context, PricingFailure.InvalidInput);
                zoneId = parsed;
            }
            var result = await handler.OverviewAsync(page, pageSize, zoneId, cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : AdminProblems.PricingFailureResult(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization(policy => policy.RequireRole("Admin")).WithName("PricingOverview").WithMetadata(new AdminPricingEndpointMetadata())
            .Produces<PricingOverviewPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);

        app.MapGet("/api/v1/pricing-tiers", async (HttpContext context, PricingAdministrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            int? page = query.TryGetValue("page", out var pVal) && int.TryParse(pVal, out var p) ? p : null;
            int? pageSize = query.TryGetValue("pageSize", out var psVal) && int.TryParse(psVal, out var ps) ? ps : null;
            Guid? zoneId = query.TryGetValue("zoneId", out var zVal) && Guid.TryParse(zVal, out var z) ? z : null;

            var result = await handler.ListAsync(page, pageSize, zoneId, cancellationToken);
            return result.Response is { } response
                ? Results.Ok(response)
                : AdminProblems.PricingFailureResult(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization(policy => policy.RequireRole("Admin")).WithName("ListPricing").WithMetadata(new AdminPricingEndpointMetadata())
            .Produces<PricingPage>().ProducesProblem(401).ProducesProblem(403);

        app.MapPost("/api/v1/pricing-tiers", async (PricingTierInput input, HttpContext context, PricingAdministrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.CreateAsync(input, cancellationToken);
            return result.Response is { } response
                ? Results.Created($"/api/v1/pricing-tiers/{response.Id}", response)
                : AdminProblems.PricingFailureResult(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization(policy => policy.RequireRole("Admin")).WithName("CreatePricing").WithMetadata(new AdminPricingEndpointMetadata())
            .Produces<PricingTierDto>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409);

        app.MapPut("/api/v1/pricing-tiers/{id:guid}", async (Guid id, PricingTierUpdateInput input, HttpContext context, PricingAdministrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.UpdateAsync(id, input, cancellationToken);
            return result.Response is { } response
                ? Results.Ok(response)
                : AdminProblems.PricingFailureResult(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization(policy => policy.RequireRole("Admin")).WithName("UpdatePricing").WithMetadata(new AdminPricingEndpointMetadata())
            .Produces<PricingTierDto>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
    }
}
