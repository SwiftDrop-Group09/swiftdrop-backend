using SwiftDrop.Merchant.Api.Application.Merchants;

namespace SwiftDrop.Merchant.Api.Presentation;

public static class MerchantProblems
{
    public static string CorrelationId(HttpContext context) =>
        (string?)context.Items["MerchantCorrelationId"] ?? context.TraceIdentifier;

    public static IResult Failure(HttpContext context, MerchantFailure failure,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var (status, code, title) = failure switch
        {
            MerchantFailure.Unauthenticated    => (401, "unauthorized",              "A verified identity is required."),
            MerchantFailure.Forbidden          => (403, "forbidden",                 "You do not have permission to perform this action."),
            MerchantFailure.InvalidInput       => (400, "validation",                "The request is invalid."),
            MerchantFailure.BusinessNotFound   => (404, "business_not_found",        "The merchant business was not found."),
            MerchantFailure.ApiKeyNotFound     => (404, "api_key_not_found",         "The API key was not found."),
            MerchantFailure.InvalidStatusTransition => (409, "invalid_status_transition", "The status transition is not allowed."),
            MerchantFailure.BusinessNotApproved => (403, "business_not_approved",   "Only approved merchants can perform this action."),
            MerchantFailure.DuplicateBusiness  => (409, "duplicate_business",        "You already have a registered business."),
            _                                  => (500, "unexpected_error",           "The request could not be completed.")
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

