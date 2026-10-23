using SwiftDrop.Delivery.Api.Application.Quotes;

namespace SwiftDrop.Delivery.Api.Presentation;

public static class QuoteProblems
{
    public static string CorrelationId(HttpContext context) =>
        (string?)context.Items["DeliveryCorrelationId"] ?? context.TraceIdentifier;

    public static IResult Failure(HttpContext context, QuoteFailure failure,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var (status, code, title) = failure switch
        {
            QuoteFailure.Unauthenticated => (401, "unauthorized", "A verified Customer identity is required."),
            QuoteFailure.Forbidden => (403, "forbidden", "The Customer role is required."),
            QuoteFailure.InvalidInput => (400, "validation", "The quote request is invalid."),
            QuoteFailure.UnsupportedZone => (422, "unsupported_zone", "Both addresses must use active service zones."),
            QuoteFailure.PricingUnavailable => (503, "pricing_unavailable", "Delivery pricing is unavailable."),
            _ => (503, "delivery_unavailable", "Delivery quote storage is unavailable.")
        };
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["correlationId"] = CorrelationId(context)
        };
        if (errors is not null)
        {
            extensions["errors"] = errors;
        }

        return Results.Problem(statusCode: status, type: "https://example.invalid/problems/" + code,
            title: title, extensions: extensions);
    }

    public static IResult Unexpected(HttpContext context) => Results.Problem(statusCode: 500,
        type: "https://example.invalid/problems/unexpected_error", title: "The request could not be completed.",
        extensions: new Dictionary<string, object?>
        {
            ["code"] = "unexpected_error",
            ["correlationId"] = CorrelationId(context)
        });
}
