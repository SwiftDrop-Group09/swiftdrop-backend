using System.Text.Json;

namespace SwiftDrop.Delivery.Api.Application.Assistant;

public sealed class McpServerHandler(AssistantToolExecutor toolExecutor)
{
    private static readonly IReadOnlyList<McpToolDefinition> Catalog =
    [
        new(
            AssistantToolNames.TrackParcel,
            "Track a parcel delivery status, location, and milestone timeline by tracking number or recent customer deliveries.",
            new
            {
                type = "object",
                properties = new
                {
                    trackingNumber = new { type = "string", description = "Parcel tracking number (e.g. SD-2026-0001)" },
                    parcelId = new { type = "string", description = "Unique parcel identifier GUID" }
                }
            }),
        new(
            AssistantToolNames.ListMyParcels,
            "List the authenticated customer's recent deliveries, routes, and status summaries.",
            new
            {
                type = "object",
                properties = new
                {
                    status = new { type = "string", description = "Filter by parcel status (Booked, Assigned, PickedUp, InTransit, Delivered, Failed, Cancelled)" },
                    search = new { type = "string", description = "Search query matching tracking number, city, or recipient" },
                    limit = new { type = "integer", description = "Maximum number of parcels to return (1-10, default 5)" }
                }
            }),
        new(
            AssistantToolNames.EstimateDeliveryFee,
            "Estimate and lock a versioned delivery fee quote between service zones based on parcel weight and package size.",
            new
            {
                type = "object",
                required = new[] { "weightKg" },
                properties = new
                {
                    weightKg = new { type = "number", description = "Parcel weight in kilograms (0 < weight <= 30)" },
                    size = new { type = "string", @enum = new[] { "Small", "Medium", "Large" }, description = "Package size dimension tier" },
                    pickupCity = new { type = "string", description = "Origin / pickup city name (e.g. Colombo, Negombo, Galle)" },
                    pickupZoneId = new { type = "string", description = "Pickup zone GUID" },
                    deliveryCity = new { type = "string", description = "Destination / delivery city name (e.g. Kandy, Galle, Jaffna)" },
                    deliveryZoneId = new { type = "string", description = "Delivery destination zone GUID" },
                    serviceSpeed = new { type = "string", @enum = new[] { "Standard", "Express" }, description = "Service delivery speed tier" }
                }
            }),
        new(
            AssistantToolNames.EstimateDeliveryTime,
            "Estimate transit duration, estimated arrival date, and SLA cutoff time between pickup and destination cities across Sri Lanka.",
            new
            {
                type = "object",
                required = new[] { "pickupCity", "deliveryCity" },
                properties = new
                {
                    pickupCity = new { type = "string", description = "Starting position / pickup city in Sri Lanka (e.g. Kandy, Colombo, Galle, Moratuwa)" },
                    deliveryCity = new { type = "string", description = "Destination city in Sri Lanka (e.g. Moratuwa, Kandy, Galle, Colombo)" },
                    serviceSpeed = new { type = "string", @enum = new[] { "Standard", "Express" }, description = "Service delivery speed tier" }
                }
            }),
        new(
            AssistantToolNames.ListServiceZones,
            "List all active SwiftDrop delivery service zones and supported delivery coverage cities.",
            new
            {
                type = "object",
                properties = new { }
            }),

        // Courier Tools
        new(
            AssistantToolNames.GetCourierAssignedDeliveries,
            "Retrieve active parcel assignments scheduled for the authenticated courier today.",
            new
            {
                type = "object",
                properties = new { }
            }),
        new(
            AssistantToolNames.GetCourierWorkSummary,
            "Retrieve shift metrics, stage counts (assigned, picked up, in transit, delivered, failed), and overdue deliveries for the authenticated courier.",
            new
            {
                type = "object",
                properties = new { }
            }),
        new(
            AssistantToolNames.GetCourierDutyStatus,
            "Retrieve on-duty / off-duty shift status, registered vehicle details, and maximum payload capacity for the authenticated courier.",
            new
            {
                type = "object",
                properties = new { }
            }),
        new(
            AssistantToolNames.GetCourierDeliveryProtocol,
            "Retrieve the official SwiftDrop courier standard operating procedure for doorstep delivery, proof of delivery capture, and exception handling.",
            new
            {
                type = "object",
                properties = new { }
            }),

        // Dispatcher Tools
        new(
            AssistantToolNames.ListUnassignedParcels,
            "Inspect the queue of unassigned parcels awaiting courier allocation with pickup, delivery, weight, and SLA details.",
            new
            {
                type = "object",
                properties = new { }
            }),
        new(
            AssistantToolNames.GetCourierWorkload,
            "Inspect active courier workload, duty state, capacity, and zone allocations across the fleet.",
            new
            {
                type = "object",
                properties = new { }
            }),
        new(
            AssistantToolNames.GetZoneDispatchSummary,
            "Inspect delivery bottlenecks, pending pickups, and fleet availability summarized by service zone.",
            new
            {
                type = "object",
                properties = new { }
            }),

        // Merchant Tools
        new(
            AssistantToolNames.ListMerchantOrders,
            "List recent commercial merchant parcels, external order references, and live fulfillment statuses.",
            new
            {
                type = "object",
                properties = new { }
            }),
        new(
            AssistantToolNames.GetMerchantIntegrationGuide,
            "Retrieve merchant REST API endpoints, API key authentication, webhook lifecycle events, and HMAC signature verification instructions.",
            new
            {
                type = "object",
                properties = new { }
            }),

        // Admin Tools
        new(
            AssistantToolNames.GetDeliverySystemReports,
            "Retrieve system-wide delivery performance reports, volume metrics, terminal counts, success rate, and daily trends.",
            new
            {
                type = "object",
                properties = new { }
            }),
        new(
            AssistantToolNames.ListPricingTariffs,
            "Inspect configured pricing tariffs, weight bands, and per-kg rates across delivery zones.",
            new
            {
                type = "object",
                properties = new { }
            }),
        new(
            AssistantToolNames.GetAdminSystemOverview,
            "Retrieve high-level system overview including today's parcel volume, unassigned queue, active on-duty couriers, and platform health.",
            new
            {
                type = "object",
                properties = new { }
            })
    ];

    public static IReadOnlyList<McpToolDefinition> GetCatalog() => Catalog;

    public async Task<McpResponse> HandleRpcAsync(McpRequest request, CancellationToken cancellationToken = default)
    {
        var id = request.Id ?? 1;

        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return new McpResponse("2.0", id, null, new McpError(-32600, "Invalid Request: missing method"));
        }

        switch (request.Method)
        {
            case "initialize":
                return new McpResponse("2.0", id, new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { } },
                    serverInfo = new
                    {
                        name = "swiftdrop-delivery-assistant",
                        version = "1.0.0"
                    }
                });

            case "ping":
                return new McpResponse("2.0", id, new { });

            case "tools/list":
                return new McpResponse("2.0", id, new { tools = Catalog });

            case "tools/call":
                return await HandleToolCallAsync(id, request.Params, cancellationToken);

            default:
                return new McpResponse("2.0", id, null, new McpError(-32601, $"Method not found: '{request.Method}'"));
        }
    }

    private async Task<McpResponse> HandleToolCallAsync(
        object id,
        JsonElement? paramsElement,
        CancellationToken cancellationToken)
    {
        if (paramsElement is null || paramsElement.Value.ValueKind != JsonValueKind.Object)
        {
            return new McpResponse("2.0", id, null, new McpError(-32602, "Invalid params: expected object containing 'name' and optional 'arguments'"));
        }

        var paramObj = paramsElement.Value;
        if (!paramObj.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
        {
            return new McpResponse("2.0", id, null, new McpError(-32602, "Invalid params: 'name' string is required"));
        }

        var toolName = nameProp.GetString()!;
        if (!AssistantToolNames.All.Contains(toolName))
        {
            return new McpResponse("2.0", id, null, new McpError(-32601, $"Unknown tool: '{toolName}'. Allowlisted tools: {string.Join(", ", AssistantToolNames.All)}"));
        }

        JsonElement? args = null;
        if (paramObj.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object)
        {
            args = argsProp;
        }

        var result = await toolExecutor.ExecuteAsync(toolName, args, cancellationToken);

        var toolResult = new McpToolCallResult(
            Content: [new McpContentItem("text", result.TextSummary)],
            StructuredContent: result.Data,
            IsError: !result.Success);

        return new McpResponse("2.0", id, toolResult);
    }
}
