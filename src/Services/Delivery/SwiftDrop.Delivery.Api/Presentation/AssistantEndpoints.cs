using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using SwiftDrop.Delivery.Api.Application.Assistant;

namespace SwiftDrop.Delivery.Api.Presentation;

public sealed class AssistantEndpointMetadata;

public static class AssistantEndpoints
{
    internal const string AnonymousRequestPolicy = "assistant-anonymous";

    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowDuplicateProperties = false,
        MaxDepth = 12
    };

    public static void MapAssistantEndpoints(this WebApplication app)
    {
        // ---------------------------------------------------------
        // 1. Assistant Chat Orchestrator (Anonymous or Authenticated)
        // ---------------------------------------------------------
        app.MapPost("/api/v1/assistant/chat", async (
            HttpContext context,
            AssistantOrchestrator orchestrator,
            IOptions<AssistantLimits> options,
            CancellationToken cancellationToken) =>
        {
            var limits = options.Value;
            if (!LimitBody(context, limits.RequestBytes))
                return TooLarge();
            if (!context.Request.HasJsonContentType())
            {
                return InvalidJson(context);
            }

            AssistantChatRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<AssistantChatRequest>(InputJson, cancellationToken);
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return TooLarge();
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or BadHttpRequestException)
            {
                return InvalidJson(context);
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.Problem(
                    statusCode: 400,
                    type: "https://example.invalid/problems/validation",
                    title: "Message cannot be empty.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "validation",
                        ["correlationId"] = (string?)context.Items["DeliveryCorrelationId"] ?? context.TraceIdentifier,
                        ["errors"] = new Dictionary<string, string[]>
                        {
                            ["message"] = ["Message content is required."]
                        }
                    });
            }

            if (request.Message.Length > limits.MessageCharacters ||
                request.ConversationId is { Length: > 128 } ||
                request.History is { } history && (history.Count > limits.HistoryEntries ||
                    history.Any(entry => entry is null || entry.Role is not ("user" or "assistant" or "model") ||
                        entry.Content is null || entry.Content.Length > limits.MessageCharacters)))
                return InvalidJson(context);

            var response = await orchestrator.ProcessChatAsync(request, cancellationToken);
            return Results.Ok(response);
        })
        .AllowAnonymous()
        .RequireRateLimiting(AnonymousRequestPolicy)
        .WithName("AssistantChat")
        .WithMetadata(new AssistantEndpointMetadata())
        .Produces<AssistantChatResponse>()
        .ProducesProblem(400)
        .ProducesProblem(500);

        // ---------------------------------------------------------
        // 2. Model Context Protocol (MCP) JSON-RPC 2.0 Endpoint
        // ---------------------------------------------------------
        app.MapPost("/api/v1/mcp", async (
            HttpContext context,
            McpServerHandler mcpHandler,
            IOptions<AssistantLimits> options,
            CancellationToken cancellationToken) =>
        {
            if (!LimitBody(context, options.Value.RequestBytes))
                return TooLarge();
            if (!context.Request.HasJsonContentType())
            {
                return InvalidJson(context);
            }

            McpRequest? rpcRequest;
            try
            {
                rpcRequest = await context.Request.ReadFromJsonAsync<McpRequest>(InputJson, cancellationToken);
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return TooLarge();
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or BadHttpRequestException)
            {
                return Results.Ok(new McpResponse("2.0", null, null, new McpError(-32700, "Parse error: Invalid JSON")));
            }

            if (rpcRequest is null)
            {
                return Results.Ok(new McpResponse("2.0", null, null, new McpError(-32600, "Invalid Request: empty body")));
            }

            var response = await mcpHandler.HandleRpcAsync(rpcRequest, cancellationToken);
            return Results.Ok(response);
        })
        .AllowAnonymous()
        .RequireRateLimiting(AnonymousRequestPolicy)
        .WithName("McpRpc")
        .WithMetadata(new AssistantEndpointMetadata())
        .Produces<McpResponse>()
        .ProducesProblem(400);

        // ---------------------------------------------------------
        // 3. MCP Tool Discovery Endpoint
        // ---------------------------------------------------------
        app.MapGet("/api/v1/mcp/tools", (McpServerHandler handler) =>
        {
            return Results.Ok(new
            {
                tools = handler.GetAllowedCatalog()
            });
        })
        .AllowAnonymous()
        .RequireRateLimiting(AnonymousRequestPolicy)
        .WithName("McpToolsCatalog")
        .WithMetadata(new AssistantEndpointMetadata())
        .Produces<object>();
    }

    private static bool LimitBody(HttpContext context, long bytes)
    {
        if (context.Request.ContentLength > bytes) return false;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = bytes;
        return true;
    }

    private static IResult TooLarge() =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Assistant request body is too large.");

    private static IResult InvalidJson(HttpContext context) => Results.Problem(
        statusCode: 400,
        type: "https://example.invalid/problems/invalid_json",
        title: "Supply a valid JSON request payload.",
        extensions: new Dictionary<string, object?>
        {
            ["code"] = "invalid_json",
            ["correlationId"] = (string?)context.Items["DeliveryCorrelationId"] ?? context.TraceIdentifier
        });
}
