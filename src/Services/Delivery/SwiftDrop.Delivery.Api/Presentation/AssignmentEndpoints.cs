using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.Dispatch;

namespace SwiftDrop.Delivery.Api.Presentation;

public sealed class AssignmentEndpointMetadata;
public static class AssignmentEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static void MapAssignments(this WebApplication app)
    {
        app.MapGet("/api/v1/deliveries/{parcelId}/assignment-options", async (string parcelId, HttpContext context,
            AssignmentHandler handler, CancellationToken cancellationToken) =>
        {
            var query = context.Request.Query;
            var invalid = query.Any(p => p.Value.Count != 1 || p.Key is not ("serviceDate" or "page" or "pageSize"));
            string? Value(string name) => query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
            var result = await handler.OptionsAsync(parcelId, new(Value("serviceDate"), Value("page"), Value("pageSize"), invalid), cancellationToken);
            return result.Response is { } response ? Results.Ok(response) : Problem(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("AssignmentOptions").WithMetadata(new AssignmentEndpointMetadata())
            .Produces<AssignmentOptions>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);

        app.MapPost("/api/v1/deliveries/assign", async (HttpContext context, ICurrentDeliveryUser user,
            AssignmentHandler handler, CancellationToken cancellationToken) =>
        {
            if (!user.IsAuthenticated || user.UserId is null || user.UserId == Guid.Empty) return Problem(context, AssignmentFailure.Unauthenticated);
            if (!user.IsOperations) return Problem(context, AssignmentFailure.Forbidden);
            var keys = context.Request.Headers["Idempotency-Key"];
            if (keys.Count != 1 || context.Request.Query.Count != 0 || !context.Request.HasJsonContentType()) return Problem(context, AssignmentFailure.InvalidInput);
            AssignCourierInput? input;
            try { input = await context.Request.ReadFromJsonAsync<AssignCourierInput>(Json, cancellationToken); }
            catch (Exception exception) when (exception is JsonException or BadHttpRequestException or NotSupportedException)
            { return Problem(context, AssignmentFailure.InvalidInput); }
            var result = await handler.AssignAsync(input, keys[0], QuoteProblems.CorrelationId(context), cancellationToken);
            return result.Response is { } response ? Results.Text(response.Body, "application/json", Encoding.UTF8, response.StatusCode)
                : Problem(context, result.Failure!.Value, result.Errors, result.CurrentVersion);
        }).RequireAuthorization().WithName("AssignCourier").WithMetadata(new AssignmentEndpointMetadata())
            .Produces<SwiftDrop.Delivery.Api.Application.Bookings.ParcelBookingResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .ProducesProblem(409).ProducesProblem(422).ProducesProblem(503);
    }

    public static IResult Problem(HttpContext context, AssignmentFailure failure,
        IReadOnlyDictionary<string, string[]>? errors = null, long? currentVersion = null)
    {
        var (status, code, title) = failure switch
        {
            AssignmentFailure.Unauthenticated => (401, "unauthorized", "A verified SwiftDrop identity is required."),
            AssignmentFailure.Forbidden => (403, "forbidden", "Dispatcher or Admin access is required."),
            AssignmentFailure.InvalidInput => (400, "validation", "The assignment request is invalid."),
            AssignmentFailure.NotFound => (404, "not_found", "The parcel or courier was not found."),
            AssignmentFailure.StaleVersion => (409, "stale_version", "Reload the parcel before assigning."),
            AssignmentFailure.InvalidTransition => (409, "invalid_transition", "Only an unassigned Created parcel can be assigned."),
            AssignmentFailure.CourierUnavailable => (422, "courier_unavailable", "The courier must be active and on duty for today's work."),
            AssignmentFailure.ZoneMismatch => (422, "zone_mismatch", "The courier does not serve this parcel's delivery zone."),
            AssignmentFailure.CapacityExceeded => (422, "capacity_exceeded", "The courier's capacity for this service date is full."),
            AssignmentFailure.KeyConflict => (409, "idempotency_conflict", "The key belongs to a different request."),
            AssignmentFailure.KeyExpired => (409, "key_expired", "The retained idempotency key has expired."),
            AssignmentFailure.OperationInProgress => (409, "operation_in_progress", "Retry the same request and key later."),
            _ => (503, "delivery_unavailable", "Delivery assignment storage is unavailable.")
        };
        var extensions = new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = QuoteProblems.CorrelationId(context) };
        if (errors is not null) extensions["errors"] = errors;
        if (failure == AssignmentFailure.StaleVersion && currentVersion is not null) extensions["currentVersion"] = currentVersion;
        if (failure == AssignmentFailure.OperationInProgress) context.Response.Headers.RetryAfter = "1";
        return Results.Problem(statusCode: status, type: "https://example.invalid/problems/" + code, title: title, extensions: extensions);
    }
}
