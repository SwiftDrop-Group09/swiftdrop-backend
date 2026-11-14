using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;

namespace SwiftDrop.Merchant.Api.Presentation;

public static class WebhookProblems
{
    public static IResult Failure(HttpContext context, WebhookFailure failure)
    {
        var correlationId = MerchantProblems.CorrelationId(context);

        var (status, title) = failure.Code switch
        {
            "unauthenticated" => (401, "A verified identity is required."),
            "forbidden" => (403, "You do not have permission to perform this action."),
            "not_found" => (404, "The requested resource was not found."),
            "conflict" => (409, "The operation could not be completed due to a conflict."),
            "invalid_input" => (400, "The request is invalid."),
            "service_unavailable" => (503, "The service is temporarily unavailable."),
            _ => (500, "An unexpected error occurred.")
        };

        var extensions = new Dictionary<string, object?>
        {
            ["code"] = failure.Code,
            ["correlationId"] = correlationId
        };

        return Results.Problem(
            statusCode: status,
            type: "https://example.invalid/problems/" + failure.Code,
            title: title,
            detail: failure.Message,
            extensions: extensions);
    }
}
