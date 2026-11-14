using System.Text.Json;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;

namespace SwiftDrop.Delivery.Api.Presentation;

public sealed class DeliveryReadEndpointMetadata;

public static class DeliveryReadEndpoints
{
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

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

        app.MapGet("/api/v1/parcels/track/{trackingNumber}", async (string trackingNumber, HttpContext context,
            CustomerTrackingHandler handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.GetPublicTrackingAsync(trackingNumber, context.Request.Query.Count != 0, cancellationToken);
            return result.Response is { } response ? Results.Ok(response)
                : DeliveryReadProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).AllowAnonymous().WithName("PublicTrack").WithMetadata(new DeliveryReadEndpointMetadata())
            .Produces<PublicTrackingSummary>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(503);

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

            return Results.Stream(async stream =>
            {
                context.Response.ContentType = "text/event-stream; charset=utf-8";
                context.Response.Headers.CacheControl = "private, no-store, no-cache, no-transform";
                context.Response.Headers["X-Accel-Buffering"] = "no";

                await using var writer = new StreamWriter(stream, leaveOpen: true);
                var lastVersion = initial.Version;
                await WriteSnapshotAsync(writer, new ParcelEventSnapshot(
                    initial.Id, initial.Status, initial.Version, initial.LastUpdatedAt), cancellationToken);

                var lastHeartbeat = DateTimeOffset.UtcNow;
                var lastReconcile = DateTimeOffset.UtcNow;
                while (!cancellationToken.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
                    catch (OperationCanceledException) { break; }

                    var now = DateTimeOffset.UtcNow;
                    var snapshotResult = await handler.GetParcelSnapshotAsync(initial.Id, cancellationToken);
                    if (snapshotResult.Response is not { } current)
                        break;

                    if (current.Version > lastVersion || now - lastReconcile >= TimeSpan.FromSeconds(30))
                    {
                        lastVersion = Math.Max(lastVersion, current.Version);
                        lastReconcile = now;
                        lastHeartbeat = now;
                        await WriteSnapshotAsync(writer, current, cancellationToken);
                    }
                    else if (now - lastHeartbeat >= TimeSpan.FromSeconds(15))
                    {
                        lastHeartbeat = now;
                        await writer.WriteAsync(": heartbeat\n\n");
                        await writer.FlushAsync(cancellationToken);
                    }
                }
            }, "text/event-stream");
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

    private static async Task WriteSnapshotAsync(StreamWriter writer, ParcelEventSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(snapshot, SseJson);
        await writer.WriteAsync($"event: snapshot\nid: {snapshot.Id}:{snapshot.Version}\ndata: {payload}\n\n");
        await writer.FlushAsync(cancellationToken);
    }
}
