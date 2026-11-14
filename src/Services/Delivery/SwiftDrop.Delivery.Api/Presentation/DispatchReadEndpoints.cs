using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Application.Dispatch;

namespace SwiftDrop.Delivery.Api.Presentation;

public static class DispatchReadEndpoints
{
    public static void MapDispatchReads(this WebApplication app)
    {
        app.MapGet("/api/v1/couriers/workload", async (HttpContext context, CourierWorkloadHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.GetWorkloadAsync(Query(context), cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("CourierWorkload").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<WorkloadPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(503);

        app.MapGet("/api/v1/couriers/{courierId}/assignments", async (string courierId, HttpContext context,
            CourierWorkloadHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.GetAssignmentsAsync(courierId, Query(context), cancellationToken);
            if (result.Response is { } response) return Results.Ok(response);
            if (result.Failure == DeliveryReadFailure.NotFound)
                return Results.Problem(statusCode: 404, title: "The courier was not found.",
                    type: "https://example.invalid/problems/not_found", extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "not_found", ["correlationId"] = QuoteProblems.CorrelationId(context)
                    });
            return DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("DispatchCourierAssignments").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<DispatchAssignmentsPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(503);
    }

    private static WorkloadQueryInput Query(HttpContext context)
    {
        var query = context.Request.Query;
        var invalid = query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("page" or "pageSize"));
        string? Value(string name) => query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
        return new(Value("page"), Value("pageSize"), invalid);
    }
}
