using SwiftDrop.Delivery.Api.Application.DeliveryReads;

namespace SwiftDrop.Delivery.Api.Presentation;

public sealed class DeliveryReadEndpointMetadata;

public static class DeliveryReadEndpoints
{
    public static void MapDeliveryReads(this WebApplication app)
    {
        app.MapGet("/api/v1/couriers/me/history", async (HttpContext context, CourierHistoryHandler handler,
            CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            var invalid = query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("page" or "pageSize"));
            string? Value(string name) => query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
            var result = await handler.ExecuteAsync(new(Value("page"), Value("pageSize"), invalid), cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("CourierHistory").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<CourierHistoryPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(503);

        app.MapGet("/api/v1/couriers/me/assignments", async (HttpContext context, DeliveryReadHandler handler,
            CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            var invalid = query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("date" or "includeOverdue" or "page" or "pageSize"));
            string? Value(string name) => query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
            var result = await handler.GetAssignmentsAsync(new AssignmentQueryInput(Value("date"), Value("includeOverdue"),
                Value("page"), Value("pageSize"), invalid), cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("CourierAssignments").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<AssignedParcelPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(503);

        app.MapGet("/api/v1/parcels/{id}", async (string id, HttpContext context, DeliveryReadHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.GetParcelAsync(id, context.Request.Query.Count != 0, cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("GetParcel").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<ParcelDetailResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(503);
    }
}
