using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Application.Serialization;

namespace SwiftDrop.Delivery.Api.Presentation;

public static class QuoteEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new ExactDecimalConverter() }
    };

    public static void MapCustomerQuotes(this WebApplication app)
    {
        app.MapPost("/api/v1/quotes", async (HttpContext context, ICurrentCustomer customer,
            QuoteEstimator estimator, CancellationToken cancellationToken) =>
        {
            // Check entry permissions before parsing private input or using storage.
            if (!customer.IsAuthenticated || customer.CustomerId is not { } ownerId || ownerId == Guid.Empty)
            {
                return QuoteProblems.Failure(context, QuoteFailure.Unauthenticated);
            }

            if (!customer.IsCustomer)
            {
                return QuoteProblems.Failure(context, QuoteFailure.Forbidden);
            }

            QuoteInput? input;
            try
            {
                if (!context.Request.HasJsonContentType())
                {
                    return InvalidJson(context);
                }

                input = await context.Request.ReadFromJsonAsync<QuoteInput>(InputJson, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or BadHttpRequestException or NotSupportedException)
            {
                // Parser exception messages may contain private input. Return only
                // the safe contract error and field label, never the supplied value.
                return InvalidJson(context);
            }

            var result = await estimator.ExecuteAsync(input, cancellationToken);
            return result.Quote is { } quote
                ? Results.Ok(quote)
                : QuoteProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization().WithName("EstimateFee").Produces<QuoteResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(422).ProducesProblem(503);
    }

    private static IResult InvalidJson(HttpContext context) => QuoteProblems.Failure(context,
        QuoteFailure.InvalidInput, new Dictionary<string, string[]>
        {
            ["body"] = ["Supply a valid JSON quote request using only the documented fields and types."]
        });
}
