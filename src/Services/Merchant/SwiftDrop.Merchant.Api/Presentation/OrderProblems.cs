using SwiftDrop.Merchant.Api.Application.Orders;

namespace SwiftDrop.Merchant.Api.Presentation;

/// <summary>ProblemDetails for order endpoints, in the same shape as <see cref="MerchantProblems"/>.</summary>
public static class OrderProblems
{
    public static IResult Failure(HttpContext context, OrderFailure failure,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var (status, code, title) = failure switch
        {
            OrderFailure.InvalidInput        => (400, "validation",           "The request is invalid."),
            OrderFailure.Forbidden           => (403, "forbidden",            "You do not have permission to perform this action."),
            OrderFailure.NotFound            => (404, "order_not_found",      "The order was not found."),
            OrderFailure.OrderConflict       => (409, "order_conflict",       "This external order was already submitted with different details."),
            OrderFailure.IdempotencyConflict => (409, "idempotency_conflict", "This Idempotency-Key was already used for a different order."),
            _                                => (500, "unexpected_error",     "The request could not be completed.")
        };

        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["correlationId"] = MerchantProblems.CorrelationId(context)
        };
        if (errors is { Count: > 0 })
        {
            extensions["errors"] = errors;
        }

        return Results.Problem(statusCode: status, type: "https://example.invalid/problems/" + code,
            title: title, extensions: extensions);
    }

    public static IResult InvalidInput(HttpContext context, string field, string message) =>
        Failure(context, OrderFailure.InvalidInput, new Dictionary<string, string[]> { [field] = [message] });
}
