using SwiftDrop.Delivery.Api.Application.Admin.Pricing;

namespace SwiftDrop.Delivery.Api.Presentation;

public sealed class AdminPricingEndpointMetadata;

public static class AdminPricingEndpoints
{
    public static void MapAdminPricing(this WebApplication app)
    {
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
        }).RequireAuthorization().WithName("ListPricing").WithMetadata(new AdminPricingEndpointMetadata())
            .Produces<PricingPage>().ProducesProblem(401).ProducesProblem(403);

        app.MapPost("/api/v1/pricing-tiers", async (PricingTierInput input, HttpContext context, PricingAdministrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.CreateAsync(input, cancellationToken);
            return result.Response is { } response
                ? Results.Created($"/api/v1/pricing-tiers/{response.Id}", response)
                : AdminProblems.PricingFailureResult(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("CreatePricing").WithMetadata(new AdminPricingEndpointMetadata())
            .Produces<PricingTierDto>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409);

        app.MapPut("/api/v1/pricing-tiers/{id:guid}", async (Guid id, PricingTierUpdateInput input, HttpContext context, PricingAdministrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.UpdateAsync(id, input, cancellationToken);
            return result.Response is { } response
                ? Results.Ok(response)
                : AdminProblems.PricingFailureResult(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("UpdatePricing").WithMetadata(new AdminPricingEndpointMetadata())
            .Produces<PricingTierDto>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
    }
}

