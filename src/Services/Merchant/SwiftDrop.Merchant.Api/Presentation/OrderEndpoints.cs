using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using SwiftDrop.Merchant.Api.Application.Orders;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication.OrderAccess;

namespace SwiftDrop.Merchant.Api.Presentation;

/// <summary>
/// Merchant order routes (FR-M04, FR-M07). Paths match swiftdrop-docs/api/rest-api.md
/// and the gateway route fixture exactly.
/// </summary>
public static class OrderEndpoints
{
    public const string OrdersPath = "/api/v1/merchant/orders";
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    private const long MaxBodyBytes = 16 * 1024;

    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void MapOrderEndpoints(this WebApplication app)
    {
        // POST — merchant system via X-API-Key (orders:write); always asynchronous 202.
        app.MapPost(OrdersPath, async (HttpContext context, SubmitMerchantOrderHandler handler,
            CancellationToken cancellationToken) =>
        {
            var idempotencyKeys = context.Request.Headers[IdempotencyKeyHeader];
            if (idempotencyKeys.Count > 1)
            {
                return OrderProblems.InvalidInput(context, IdempotencyKeyHeader, "Send exactly one Idempotency-Key header.");
            }

            var (input, bodyError) = await ReadBodyAsync<MerchantOrderInput>(context, cancellationToken);
            if (bodyError is not null)
            {
                return bodyError;
            }

            var result = await handler.SubmitAsync(input, idempotencyKeys.FirstOrDefault(), cancellationToken);
            return result.Value is { } order
                ? Results.Accepted($"{OrdersPath}/{order.Id}", order)
                : OrderProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization(MerchantAuthorizationPolicies.OrdersWrite).WithName("SubmitMerchantOrder")
          .Produces<OrderJobResponse>(202)
          .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409);

        // GET list — owner JWT or X-API-Key (orders:read); scoped to the caller's business.
        app.MapGet(OrdersPath, async (HttpContext context, MerchantOrderQueryHandler handler,
            string? page, string? pageSize, string? state, CancellationToken cancellationToken) =>
        {
            var result = await handler.ListAsync(new OrderListQuery(page, pageSize, state), cancellationToken);
            return result.Value is { } orders
                ? Results.Ok(orders)
                : OrderProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization(MerchantAuthorizationPolicies.OrdersRead).WithName("ListMerchantOrders")
          .Produces<OrderJobPage>(200)
          .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);

        // GET one — foreign or missing orders are both 404.
        app.MapGet($"{OrdersPath}/{{id:guid}}", async (Guid id, HttpContext context, MerchantOrderQueryHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.GetAsync(id, cancellationToken);
            return result.Value is { } order
                ? Results.Ok(order)
                : OrderProblems.Failure(context, result.Failure!.Value, result.Errors);
        }).RequireAuthorization(MerchantAuthorizationPolicies.OrdersRead).WithName("GetMerchantOrder")
          .Produces<OrderJobResponse>(200)
          .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
    }

    /// <summary>Reads a strict, size-limited JSON body. Any malformed body becomes a 400 without detail leakage.</summary>
    private static async Task<(T? Value, IResult? Error)> ReadBodyAsync<T>(HttpContext context, CancellationToken cancellationToken)
        where T : class
    {
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return (null, OrderProblems.InvalidInput(context, "body", $"The request body cannot exceed {MaxBodyBytes} bytes."));
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
        {
            sizeLimit.MaxRequestBodySize = MaxBodyBytes;
        }

        if (!context.Request.HasJsonContentType())
        {
            return (null, InvalidJson(context));
        }

        try
        {
            return (await context.Request.ReadFromJsonAsync<T>(InputJson, cancellationToken), null);
        }
        catch (Exception exception) when (exception is JsonException or BadHttpRequestException or NotSupportedException)
        {
            return (null, InvalidJson(context));
        }
    }

    private static IResult InvalidJson(HttpContext context) =>
        OrderProblems.InvalidInput(context, "body", "Supply a valid JSON request body.");
}
