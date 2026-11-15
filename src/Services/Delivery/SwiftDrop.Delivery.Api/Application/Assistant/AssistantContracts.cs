using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwiftDrop.Delivery.Api.Application.Assistant;

public static class AssistantToolNames
{
    public const string TrackParcel = "track_parcel";
    public const string ListMyParcels = "list_my_parcels";
    public const string EstimateDeliveryFee = "estimate_delivery_fee";
    public const string ListServiceZones = "list_service_zones";

    public static readonly IReadOnlyList<string> All =
    [
        TrackParcel,
        ListMyParcels,
        EstimateDeliveryFee,
        ListServiceZones
    ];
}

// -------------------------------------------------------------
// Tool Input Models
// -------------------------------------------------------------

public sealed record TrackParcelInput(
    string? TrackingNumber = null,
    string? ParcelId = null);

public sealed record ListMyParcelsInput(
    string? Status = null,
    string? Search = null,
    int? Limit = null);

public sealed record EstimateFeeInput(
    decimal? WeightKg = null,
    string? Size = null,
    string? PickupCity = null,
    string? PickupZoneId = null,
    string? DeliveryCity = null,
    string? DeliveryZoneId = null,
    string? ServiceSpeed = null);

public sealed record ListServiceZonesInput();

// -------------------------------------------------------------
// Tool Output Models
// -------------------------------------------------------------

public sealed record ToolExecutionResult(
    string ToolName,
    bool Success,
    object? Data,
    string TextSummary,
    string? Error = null);

// -------------------------------------------------------------
// UI Response Cards
// -------------------------------------------------------------

public sealed record MilestoneStep(
    string Status,
    string Label,
    DateTimeOffset? Timestamp,
    bool Completed,
    bool Current);

public sealed record TrackingCardData(
    string TrackingNumber,
    string Status,
    string OriginCity,
    string DestinationCity,
    DateTimeOffset LastUpdatedAt,
    IReadOnlyList<MilestoneStep> Steps,
    bool CanViewReceipt,
    string? ParcelId = null);

public sealed record ParcelSummaryItem(
    string TrackingNumber,
    string Status,
    string Route,
    decimal WeightKg,
    string Size,
    DateTimeOffset CreatedAt,
    string? ParcelId = null);

public sealed record ParcelListCardData(
    int TotalCount,
    IReadOnlyList<ParcelSummaryItem> Parcels);

public sealed record QuoteCardData(
    Guid? QuoteId,
    decimal Total,
    string Currency,
    string PricingVersion,
    DateTimeOffset? ExpiresAt,
    decimal WeightKg,
    string Size,
    string PickupCity,
    string DeliveryCity,
    string BookingDeepLink);

public sealed record ZoneCoverageItem(
    Guid ZoneId,
    string Name,
    IReadOnlyList<string> Cities,
    bool Active);

public sealed record ZonesCardData(
    IReadOnlyList<ZoneCoverageItem> Zones,
    IReadOnlyList<string> SupportedCities);

public sealed record AssistantCards(
    TrackingCardData? Tracking = null,
    ParcelListCardData? ParcelList = null,
    QuoteCardData? Quote = null,
    ZonesCardData? Zones = null);

// -------------------------------------------------------------
// Assistant Chat Request / Response Models
// -------------------------------------------------------------

public sealed record ChatMessage(
    string Role,
    string Content);

public sealed record AssistantChatRequest(
    string Message,
    string? ConversationId = null,
    IReadOnlyList<ChatMessage>? History = null);

public sealed record AssistantToolCallTrace(
    string ToolName,
    object? Arguments,
    string Status,
    long DurationMs,
    string Summary);

public sealed record GuardrailResult(
    bool Blocked,
    string Reason);

public sealed record AssistantChatResponse(
    string Reply,
    string Mode,
    IReadOnlyList<AssistantToolCallTrace> ToolCalls,
    AssistantCards? Cards = null,
    IReadOnlyList<string>? SuggestedPrompts = null,
    GuardrailResult? Guardrail = null);

// -------------------------------------------------------------
// Model Context Protocol (MCP) JSON-RPC 2.0 Models
// -------------------------------------------------------------

public sealed record McpRequest(
    [property: JsonPropertyName("jsonrpc")] string? Jsonrpc,
    [property: JsonPropertyName("id")] object? Id,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("params")] JsonElement? Params);

public sealed record McpResponse(
    [property: JsonPropertyName("jsonrpc")] string Jsonrpc,
    [property: JsonPropertyName("id")] object? Id,
    [property: JsonPropertyName("result")] object? Result = null,
    [property: JsonPropertyName("error")] McpError? Error = null);

public sealed record McpError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] object? Data = null);

public sealed record McpToolDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] object InputSchema);

public sealed record McpContentItem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

public sealed record McpToolCallResult(
    [property: JsonPropertyName("content")] IReadOnlyList<McpContentItem> Content,
    [property: JsonPropertyName("structuredContent")] object? StructuredContent = null,
    [property: JsonPropertyName("isError")] bool IsError = false);
