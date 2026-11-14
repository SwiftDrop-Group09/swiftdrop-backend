using SwiftDrop.Delivery.Api.Application.Admin.Zones;

namespace SwiftDrop.Delivery.Api.Presentation;

public sealed class AdminZoneEndpointMetadata;

public static class AdminZoneEndpoints
{
    public static void MapAdminZones(this WebApplication app)
    {
        app.MapGet("/api/v1/zones", async (HttpContext context, ZoneAdministrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            int? page = query.TryGetValue("page", out var pVal) && int.TryParse(pVal, out var p) ? p : null;
            int? pageSize = query.TryGetValue("pageSize", out var psVal) && int.TryParse(psVal, out var ps) ? ps : null;

            var result = await handler.ListAsync(page, pageSize, cancellationToken);
            return result.Response is { } response
                ? Results.Ok(response)
                : AdminProblems.ZoneFailureResult(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization(policy => policy.RequireRole("Admin")).WithName("ListZones").WithMetadata(new AdminZoneEndpointMetadata())
            .Produces<ZonePage>().ProducesProblem(401).ProducesProblem(403);

        app.MapPost("/api/v1/zones", async (ZoneInput input, HttpContext context, ZoneAdministrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.CreateAsync(input, cancellationToken);
            return result.Response is { } response
                ? Results.Created($"/api/v1/zones/{response.Id}", response)
                : AdminProblems.ZoneFailureResult(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization(policy => policy.RequireRole("Admin")).WithName("CreateZone").WithMetadata(new AdminZoneEndpointMetadata())
            .Produces<ZoneDto>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409);

        app.MapPut("/api/v1/zones/{id:guid}", async (Guid id, ZoneInput input, HttpContext context, ZoneAdministrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.UpdateAsync(id, input, cancellationToken);
            return result.Response is { } response
                ? Results.Ok(response)
                : AdminProblems.ZoneFailureResult(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization(policy => policy.RequireRole("Admin")).WithName("UpdateZone").WithMetadata(new AdminZoneEndpointMetadata())
            .Produces<ZoneDto>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        app.MapGet("/api/v1/zones/summary", async (HttpContext context, ZoneAdministrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            DateTimeOffset? from = query.TryGetValue("from", out var fVal) && DateTimeOffset.TryParse(fVal, out var f) ? f : null;
            DateTimeOffset? to = query.TryGetValue("to", out var tVal) && DateTimeOffset.TryParse(tVal, out var t) ? t : null;

            var result = await handler.GetSummaryAsync(from, to, cancellationToken);
            return result.Response is { } response
                ? Results.Ok(response)
                : AdminProblems.ZoneFailureResult(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization(policy => policy.RequireRole("Admin", "Dispatcher")).WithName("ZoneSummary").WithMetadata(new AdminZoneEndpointMetadata())
            .Produces<ZoneSummaryResponse>().ProducesProblem(401).ProducesProblem(403);
    }
}
