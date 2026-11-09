using SwiftDrop.Delivery.Api.Application.Admin.Reports;

namespace SwiftDrop.Delivery.Api.Presentation;

public sealed class AdminReportEndpointMetadata;

public static class AdminReportEndpoints
{
    public static void MapAdminReports(this WebApplication app)
    {
        app.MapGet("/api/v1/reports/deliveries", async (HttpContext context, DeliveryReportHandler handler,
            CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            string? from = query.TryGetValue("from", out var fVal) ? fVal.ToString() : null;
            string? to = query.TryGetValue("to", out var tVal) ? tVal.ToString() : null;

            var result = await handler.ExecuteAsync(from, to, cancellationToken);
            return result.Response is { } response
                ? Results.Ok(response)
                : AdminProblems.ReportFailureResult(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("DeliveryReport").WithMetadata(new AdminReportEndpointMetadata())
            .Produces<DeliveryReportResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
    }
}

