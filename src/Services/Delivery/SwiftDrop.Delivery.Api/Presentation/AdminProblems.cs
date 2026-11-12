using SwiftDrop.Delivery.Api.Application.Admin.Pricing;
using SwiftDrop.Delivery.Api.Application.Admin.Reports;
using SwiftDrop.Delivery.Api.Application.Admin.Zones;

namespace SwiftDrop.Delivery.Api.Presentation;

public static class AdminProblems
{
    public static IResult ZoneFailureResult(HttpContext context, ZoneFailure failure,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var (status, code, title) = failure switch
        {
            ZoneFailure.Unauthenticated => (401, "unauthorized", "A verified SwiftDrop identity is required."),
            ZoneFailure.Forbidden => (403, "forbidden", "Only administrators can configure delivery zones."),
            ZoneFailure.NotFound => (404, "not_found", "The specified delivery zone was not found."),
            ZoneFailure.Conflict => (409, "conflict", "The zone changed or its name already exists. Refresh before updating."),
            ZoneFailure.OverlappingPostalCodes => (400, "overlapping_postal_codes", "Postal codes must not overlap across active delivery zones."),
            _ => (400, "validation", "The zone input is invalid.")
        };

        return BuildProblem(context, status, code, title, errors);
    }

    public static IResult PricingFailureResult(HttpContext context, PricingFailure failure,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var (status, code, title) = failure switch
        {
            PricingFailure.Unauthenticated => (401, "unauthorized", "A verified SwiftDrop identity is required."),
            PricingFailure.Forbidden => (403, "forbidden", "Only administrators can configure pricing tiers."),
            PricingFailure.NotFound => (404, "not_found", "The specified pricing tier was not found."),
            PricingFailure.Conflict => (409, "conflict", "A pricing tier for this zone, size and version already exists."),
            _ => (400, "validation", "The pricing input is invalid.")
        };

        return BuildProblem(context, status, code, title, errors);
    }

    public static IResult ReportFailureResult(HttpContext context, ReportFailure failure,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var (status, code, title) = failure switch
        {
            ReportFailure.Unauthenticated => (401, "unauthorized", "A verified SwiftDrop identity is required."),
            ReportFailure.Forbidden => (403, "forbidden", "Only administrators can view delivery reports."),
            _ => (400, "invalid_date_range", "The report date range query is invalid.")
        };

        return BuildProblem(context, status, code, title, errors);
    }

    public static IResult Unavailable(HttpContext context) => BuildProblem(context, 503, "delivery_unavailable", "Delivery administration is temporarily unavailable.");
    public static IResult Conflict(HttpContext context) => BuildProblem(context, 409, "conflict", "The configuration changed. Refresh before trying again.");

    private static IResult BuildProblem(HttpContext context, int status, string code, string title,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
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
