using SwiftDrop.Delivery.Api.Application.CourierStatus;

namespace SwiftDrop.Delivery.Api.Presentation;

public static class CourierStatusProblems
{
    public static IResult Failure(HttpContext context, CourierStatusFailure failure,
        IReadOnlyDictionary<string, string[]>? errors = null, long? currentVersion = null)
    {
        var (status, code, title) = failure switch
        {
            CourierStatusFailure.Unauthenticated => (401, "unauthorized", "A verified SwiftDrop identity is required."),
            CourierStatusFailure.Forbidden => (403, "forbidden", "The Courier role is required."),
            CourierStatusFailure.InvalidInput => (400, "validation", "The status request is invalid."),
            CourierStatusFailure.NotFound => (404, "not_found", "The parcel was not found."),
            CourierStatusFailure.StaleVersion => (409, "stale_version", "Reload the parcel before choosing a new action."),
            CourierStatusFailure.InvalidTransition => (409, "invalid_transition", "This Courier transition is not allowed."),
            CourierStatusFailure.KeyConflict => (409, "idempotency_conflict", "The key belongs to a different request."),
            CourierStatusFailure.KeyExpired => (409, "key_expired", "The retained idempotency key has expired."),
            CourierStatusFailure.OperationInProgress => (409, "operation_in_progress", "Retry the same request and key later."),
            _ => (503, "delivery_unavailable", "Delivery status storage is unavailable.")
        };
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code, ["correlationId"] = QuoteProblems.CorrelationId(context)
        };
        if (errors is not null) extensions["errors"] = errors;
        if (failure == CourierStatusFailure.StaleVersion && currentVersion is not null) extensions["currentVersion"] = currentVersion;
        if (failure == CourierStatusFailure.OperationInProgress) context.Response.Headers.RetryAfter = "1";
        return Results.Problem(statusCode: status, type: "https://example.invalid/problems/" + code, title: title, extensions: extensions);
    }
}
