using SwiftDrop.Delivery.Api.Application.DeliveryReads;

namespace SwiftDrop.Delivery.Api.Presentation;

public sealed class DeliveryReadEndpointMetadata;

public static class DeliveryReadEndpoints
{
    internal const string PublicTrackingStreamsPolicy = "public-tracking-streams";

    public static void MapDeliveryReads(this WebApplication app)
    {
        app.MapGet("/api/v1/parcels/summary", async (HttpContext context, CustomerTrackingHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.GetSummaryAsync(context.Request.Query.Count != 0, cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("CustomerDeliverySummary").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<CustomerDeliverySummary>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(503);

        app.MapGet("/api/v1/couriers/me/summary", async (HttpContext context, CourierSummaryHandler handler,
            CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            var invalid = query.Any(pair => pair.Value.Count != 1
                || pair.Key is not ("date" or "includeOverdue" or "overdueOnly" or "search"));
            string? Value(string name) => query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
            var result = await handler.ExecuteAsync(new(Value("date"), Value("includeOverdue"), Value("overdueOnly"), Value("search"), invalid), cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("CourierWorkSummary").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<CourierWorkSummary>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(503);

        app.MapGet("/api/v1/couriers/me/history/{id}", async (string id, HttpContext context, CourierHistoryDetailHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.ExecuteAsync(id, context.Request.Query.Count != 0, cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("CourierHistoryDetail").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<CourierHistoryDetailResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(503);

        app.MapGet("/api/v1/couriers/me/history", async (HttpContext context, CourierHistoryHandler handler,
            CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            var invalid = query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("page" or "pageSize" or "status" or "date" or "search"));
            string? Value(string name) => query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
            var result = await handler.ExecuteAsync(new(Value("page"), Value("pageSize"), invalid, Value("status"), Value("date"), Value("search")), cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("CourierHistory").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<CourierHistoryPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(503);

        app.MapGet("/api/v1/couriers/me/assignments", async (HttpContext context, DeliveryReadHandler handler,
            CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            var invalid = query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("date" or "includeOverdue" or "page" or "pageSize" or "status" or "search" or "overdueOnly"));
            string? Value(string name) => query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
            var result = await handler.GetAssignmentsAsync(new AssignmentQueryInput(Value("date"), Value("includeOverdue"),
                Value("page"), Value("pageSize"), invalid, Value("status"), Value("search"), Value("overdueOnly")), cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("CourierAssignments").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<AssignedParcelPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(503);

        app.MapGet("/api/v1/parcels/track/{trackingNumber}", async (string trackingNumber, HttpContext context,
            CustomerTrackingHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.GetPublicTrackingAsync(trackingNumber, context.Request.Query.Count != 0, cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).AllowAnonymous().WithName("PublicTrack").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<PublicTrackingSummary>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(503);

        app.MapGet("/api/v1/parcels/track/{trackingNumber}/events", async (string trackingNumber, HttpContext context,
            CustomerTrackingHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.GetPublicTrackingSnapshotAsync(trackingNumber, context.Request.Query.Count != 0,
                cancellationToken);
            if (result.Response is not { } initial)
                return DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);

            static TrackingEventStream.Snapshot Snapshot(PublicTrackingEventSnapshot item) =>
                new(item.Version, item.State, IsTerminal: item.IsTerminal);

            return TrackingEventStream.Create(context, Snapshot(initial), async ct =>
            {
                var current = await handler.GetPublicTrackingSnapshotAsync(initial.State.TrackingNumber, cancellationToken: ct);
                return current.Response is { } snapshot ? Snapshot(snapshot) : null;
            }, cancellationToken);
        }).AllowAnonymous().RequireRateLimiting(PublicTrackingStreamsPolicy).WithName("PublicTrackingEvents")
            .WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces(200, contentType: "text/event-stream").ProducesProblem(400).ProducesProblem(404)
            .ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/api/v1/parcels", async (HttpContext context, CustomerTrackingHandler handler,
            CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            var invalid = query.Any(pair => pair.Value.Count != 1
                || pair.Key is not ("status" or "from" or "to" or "search" or "page" or "pageSize" or "zoneId"));
            string? Value(string name) => query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
            var result = await handler.ListParcelsAsync(new CustomerParcelQueryInput(
                Value("status"), Value("from"), Value("to"), Value("search"),
                Value("page"), Value("pageSize"), invalid, ZoneId: Value("zoneId")), cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("ListParcels").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<CustomerParcelPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(503);

        app.MapGet("/api/v1/parcels/{id}", async (string id, HttpContext context, DeliveryReadHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.GetParcelAsync(id, context.Request.Query.Count != 0, cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("GetParcel").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<ParcelDetailResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(503);

        app.MapGet("/api/v1/parcels/{id}/events", async (string id, HttpContext context,
            CustomerTrackingHandler handler, CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            var format = query.TryGetValue("format", out var formatValues) && formatValues.Count == 1
                ? formatValues[0] : null;
            var invalid = query.Any(pair => pair.Value.Count != 1 || pair.Key is not "format")
                || (format is not null && format is not ("json" or "sse"));

            if (WantsJson(context, "text/event-stream", format))
            {
                var jsonResult = await handler.GetParcelEventsAsync(id, invalid, cancellationToken);
                return jsonResult.Response is { } response ? Results.Ok(response)
                    : DeliveryReadProblems.Failure(context, jsonResult.Failure!.Value, jsonResult.Errors);
            }

            if (invalid)
                return DeliveryReadProblems.Failure(context, DeliveryReadFailure.InvalidInput,
                    new Dictionary<string, string[]> { ["query"] = ["Supply a valid query parameter."] });

            var initialResult = await handler.GetParcelEventsAsync(id, false, cancellationToken);
            if (initialResult.Response is not { } initial)
                return DeliveryReadProblems.Failure(context, initialResult.Failure!.Value, initialResult.Errors);

            static TrackingEventStream.Snapshot Snapshot(ParcelEventSnapshot item) =>
                new(item.Version, item, $"{item.Id}:{item.Version}");

            var first = new ParcelEventSnapshot(initial.Id, initial.Status, initial.Version, initial.LastUpdatedAt);
            return TrackingEventStream.Create(context, Snapshot(first), async ct =>
            {
                var current = await handler.GetParcelSnapshotAsync(initial.Id, ct);
                return current.Response is { } snapshot ? Snapshot(snapshot) : null;
            }, cancellationToken);
        }).RequireAuthorization().WithName("ParcelEvents").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<ParcelEventsResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(503);

        app.MapGet("/api/v1/parcels/{id}/receipt", async (string id, HttpContext context,
            CustomerTrackingHandler handler, CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            var format = query.TryGetValue("format", out var formatValues) && formatValues.Count == 1
                ? formatValues[0] : null;
            var invalid = query.Any(pair => pair.Value.Count != 1 || pair.Key is not "format")
                || (format is not null && format is not ("json" or "pdf"));

            if (WantsJson(context, "application/pdf", format))
            {
                var jsonResult = await handler.GetReceiptAsync(id, invalid, cancellationToken);
                return jsonResult.Response is { } response ? Results.Ok(response)
                    : DeliveryReadProblems.Failure(context, jsonResult.Failure!.Value, jsonResult.Errors);
            }

            var pdfResult = await handler.GetReceiptPdfAsync(id, invalid, cancellationToken);
            return pdfResult.Response is { } pdf
                ? Results.File(pdf.Content, "application/pdf", pdf.FileName)
                : DeliveryReadProblems.Failure(context, pdfResult.Failure!.Value, pdfResult.Errors);
        }).RequireAuthorization().WithName("DownloadReceipt").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<ParcelReceiptResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);

        app.MapGet("/api/v1/parcels/{id}/pod", async (string id, HttpContext context,
            CustomerTrackingHandler handler, CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            var format = query.TryGetValue("format", out var formatValues) && formatValues.Count == 1
                ? formatValues[0] : null;
            var invalid = query.Any(pair => pair.Value.Count != 1 || pair.Key is not "format")
                || (format is not null && format is not ("json" or "image"));

            if (WantsJson(context, "image/", format))
            {
                var jsonResult = await handler.GetProofAsync(id, invalid, cancellationToken);
                return jsonResult.Response is { } response ? Results.Ok(response)
                    : DeliveryReadProblems.Failure(context, jsonResult.Failure!.Value, jsonResult.Errors);
            }

            var imageResult = await handler.GetProofImageAsync(id, invalid, cancellationToken);
            return imageResult.Response is { } image
                ? Results.File(image.Content, image.ContentType)
                : DeliveryReadProblems.Failure(context, imageResult.Failure!.Value, imageResult.Errors);
        }).RequireAuthorization().WithName("DownloadProof").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<ProofOfDeliveryResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
    }

    private static bool WantsJson(HttpContext context, string primaryMediaPrefix, string? format)
    {
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(format))
            return false;
        var accept = context.Request.Headers.Accept.ToString();
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains(primaryMediaPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
