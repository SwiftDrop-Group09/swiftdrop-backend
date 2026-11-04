using SwiftDrop.Delivery.Api.Application.DeliveryReads;

namespace SwiftDrop.Delivery.Api.Presentation;

public static class DeliveryReadProblems
{
    public static IResult Failure(HttpContext context, DeliveryReadFailure failure,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var (status, code, title) = failure switch
        {
            DeliveryReadFailure.Unauthenticated => (401, "unauthorized", "A verified SwiftDrop identity is required."),
            DeliveryReadFailure.Forbidden => (403, "forbidden", "This role cannot use this delivery read operation."),
            DeliveryReadFailure.InvalidInput => (400, "validation", "The delivery query is invalid."),
            DeliveryReadFailure.NotFound => (404, "not_found", "The parcel was not found."),
            _ => (503, "delivery_unavailable", "Delivery information is unavailable.")
        };
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["correlationId"] = QuoteProblems.CorrelationId(context)
        };
        if (errors is not null) extensions["errors"] = errors;
        return Results.Problem(statusCode: status, type: "https://example.invalid/problems/" + code,
            title: title, extensions: extensions);
    }
}
