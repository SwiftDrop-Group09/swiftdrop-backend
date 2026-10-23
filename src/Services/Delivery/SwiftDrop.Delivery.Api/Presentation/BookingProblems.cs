using SwiftDrop.Delivery.Api.Application.Bookings;

namespace SwiftDrop.Delivery.Api.Presentation;

public static class BookingProblems
{
    public static IResult Failure(HttpContext context, BookingFailure failure,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var (status, code, title) = failure switch
        {
            BookingFailure.Unauthenticated => (401, "unauthorized", "A verified Customer identity is required."),
            BookingFailure.Forbidden => (403, "forbidden", "The Customer role is required."),
            BookingFailure.InvalidInput => (400, "validation", "The booking confirmation is invalid."),
            BookingFailure.NotFound => (404, "not_found", "The quote was not found."),
            BookingFailure.QuoteExpired => (409, "quote_expired", "The quote has expired. Obtain and confirm a new quote."),
            BookingFailure.QuoteConsumed => (409, "quote_consumed", "The quote has already been confirmed."),
            BookingFailure.PriceChanged => (409, "price_mismatch", "The accepted total does not match the quote."),
            BookingFailure.QuoteConflict => (409, "quote_conflict", "The retained quote cannot be confirmed."),
            BookingFailure.KeyConflict => (409, "idempotency_conflict", "The key was used for a different confirmation."),
            BookingFailure.KeyExpired => (409, "key_expired", "The retained idempotency key has expired."),
            BookingFailure.OperationInProgress => (409, "operation_in_progress", "The operation is in progress. Retry using the same key."),
            _ => (503, "delivery_unavailable", "Delivery booking storage is unavailable.")
        };
        if (failure == BookingFailure.OperationInProgress)
        {
            context.Response.Headers.RetryAfter = "1";
        }

        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["correlationId"] = QuoteProblems.CorrelationId(context)
        };
        if (errors is not null)
        {
            extensions["errors"] = errors;
        }

        return Results.Problem(statusCode: status, type: "https://example.invalid/problems/" + code,
            title: title, extensions: extensions);
    }
}
