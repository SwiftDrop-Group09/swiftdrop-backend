using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftDrop.Delivery.Api.Application.Assistant;

namespace SwiftDrop.Delivery.Api.Presentation;

public sealed class AssistantEndpointMetadata;

public static class AssistantEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowDuplicateProperties = false
    };

    public static void MapAssistantEndpoints(this WebApplication app)
    {
        // ---------------------------------------------------------
        // 1. Assistant Chat Orchestrator (Anonymous or Authenticated)
        // ---------------------------------------------------------
        app.MapPost("/api/v1/assistant/chat", async (
            HttpContext context,
            AssistantOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            if (!context.Request.HasJsonContentType())
            {
                return InvalidJson(context);
            }

            AssistantChatRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<AssistantChatRequest>(InputJson, cancellationToken);
            }
            catch (Exception)
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

            var response = await orchestrator.ProcessChatAsync(request, cancellationToken);
            return Results.Ok(response);
        })
        .AllowAnonymous()
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
            CancellationToken cancellationToken) =>
        {
            if (!context.Request.HasJsonContentType())
            {
                return InvalidJson(context);
            }

            McpRequest? rpcRequest;
            try
            {
                rpcRequest = await context.Request.ReadFromJsonAsync<McpRequest>(InputJson, cancellationToken);
            }
            catch (Exception)
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
        .WithName("McpRpc")
        .WithMetadata(new AssistantEndpointMetadata())
        .Produces<McpResponse>()
        .ProducesProblem(400);

        // ---------------------------------------------------------
        // 3. MCP Tool Discovery Endpoint
        // ---------------------------------------------------------
        app.MapGet("/api/v1/mcp/tools", () =>
        {
            return Results.Ok(new
            {
                tools = McpServerHandler.GetCatalog()
            });
        })
        .AllowAnonymous()
        .WithName("McpToolsCatalog")
        .WithMetadata(new AssistantEndpointMetadata())
        .Produces<object>();
    }

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
