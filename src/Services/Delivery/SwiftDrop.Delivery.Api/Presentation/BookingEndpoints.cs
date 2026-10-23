using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.Serialization;

namespace SwiftDrop.Delivery.Api.Presentation;

public static class BookingEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new ExactDecimalConverter() }
    };

    public static void MapCustomerBookings(this WebApplication app)
    {
        app.MapPost("/api/v1/parcels", async (HttpContext context, ICurrentCustomer customer,
            CustomerBookingHandler handler, CancellationToken cancellationToken) =>
        {
            if (!customer.IsAuthenticated || customer.CustomerId is not { } ownerId || ownerId == Guid.Empty)
            {
                return BookingProblems.Failure(context, BookingFailure.Unauthenticated);
            }

            if (!customer.IsCustomer)
            {
                return BookingProblems.Failure(context, BookingFailure.Forbidden);
            }

            var suppliedKey = context.Request.Headers["Idempotency-Key"];
            if (suppliedKey.Count != 1)
            {
                return BookingProblems.Failure(context, BookingFailure.InvalidInput,
                    new Dictionary<string, string[]> { ["Idempotency-Key"] = ["Supply one idempotency key."] });
            }

            ConfirmInput? input;
            try
            {
                if (!context.Request.HasJsonContentType())
                {
                    return InvalidJson(context);
                }

                input = await context.Request.ReadFromJsonAsync<ConfirmInput>(InputJson, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or BadHttpRequestException or NotSupportedException)
            {
                return InvalidJson(context);
            }

            var result = await handler.ExecuteAsync(input, suppliedKey[0], QuoteProblems.CorrelationId(context), cancellationToken);
            if (result.Response is not { } response)
            {
                return BookingProblems.Failure(context, result.Failure!.Value, result.Errors);
            }

            context.Response.Headers.Location = response.Location;
            // Serialize once in the use case, persist that exact body, and return
            // the same bytes on both the initial request and committed replays.
            return Results.Text(response.Body, "application/json", Encoding.UTF8, response.StatusCode);
        }).RequireAuthorization().WithName("CreateParcel").Produces<ParcelBookingResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
    }

    private static IResult InvalidJson(HttpContext context) => BookingProblems.Failure(context,
        BookingFailure.InvalidInput, new Dictionary<string, string[]>
        {
            ["body"] = ["Supply a valid JSON confirmation using only quoteId and acceptTotal."]
        });
}
