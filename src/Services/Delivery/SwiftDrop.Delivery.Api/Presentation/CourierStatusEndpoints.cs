using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.CourierStatus;

namespace SwiftDrop.Delivery.Api.Presentation;

public sealed class CourierStatusEndpointMetadata;

public static class CourierStatusEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void MapCourierStatus(this WebApplication app)
    {
        app.MapPut("/api/v1/deliveries/{id}/status", async (string id, HttpContext context,
            ICurrentDeliveryUser actor, CourierStatusHandler handler, CancellationToken cancellationToken) =>
        {
            if (!actor.IsAuthenticated || actor.UserId is not { } userId || userId == Guid.Empty)
                return CourierStatusProblems.Failure(context, CourierStatusFailure.Unauthenticated);
            if (!actor.IsCourier) return CourierStatusProblems.Failure(context, CourierStatusFailure.Forbidden);
            var key = context.Request.Headers["Idempotency-Key"];
            if (key.Count != 1) return CourierStatusProblems.Failure(context, CourierStatusFailure.InvalidInput,
                new Dictionary<string, string[]> { ["Idempotency-Key"] = ["Supply one idempotency key."] });
            if (context.Request.Query.Count != 0) return InvalidBody(context);
            CourierStatusInput? input;
            try
            {
                if (!context.Request.HasJsonContentType()) return InvalidBody(context);
                input = await context.Request.ReadFromJsonAsync<CourierStatusInput>(InputJson, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or BadHttpRequestException or NotSupportedException)
            {
                return InvalidBody(context);
            }
            var result = await handler.ExecuteAsync(id, input, key[0], QuoteProblems.CorrelationId(context), cancellationToken);
            return result.Response is { } response
                ? Results.Text(response.Body, "application/json", Encoding.UTF8, response.StatusCode)
                : CourierStatusProblems.Failure(context, result.Failure!.Value, result.Errors, result.CurrentVersion);
        }).RequireAuthorization().WithName("UpdateDeliveryStatus").WithMetadata(new CourierStatusEndpointMetadata())
            .Produces<SwiftDrop.Delivery.Api.Application.Bookings.ParcelBookingResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
    }

    private static IResult InvalidBody(HttpContext context) => CourierStatusProblems.Failure(context, CourierStatusFailure.InvalidInput,
        new Dictionary<string, string[]> { ["body"] = ["Supply valid JSON using status, expectedVersion, notes and failureReason only."] });
}
