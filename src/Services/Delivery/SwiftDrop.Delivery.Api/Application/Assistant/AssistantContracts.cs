using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwiftDrop.Delivery.Api.Application.Assistant;

public static class AssistantToolNames
{
    // Customer / Public Tools
    public const string TrackParcel = "track_parcel";
    public const string ListMyParcels = "list_my_parcels";
    public const string EstimateDeliveryFee = "estimate_delivery_fee";
    public const string ListServiceZones = "list_service_zones";

    // Courier Tools
    public const string GetCourierAssignedDeliveries = "get_courier_assigned_deliveries";
    public const string GetCourierWorkSummary = "get_courier_work_summary";
    public const string GetCourierDutyStatus = "get_courier_duty_status";
    public const string GetCourierDeliveryProtocol = "get_courier_delivery_protocol";

    // Dispatcher Tools
    public const string ListUnassignedParcels = "list_unassigned_parcels";
    public const string GetCourierWorkload = "get_courier_workload";
    public const string GetZoneDispatchSummary = "get_zone_dispatch_summary";

    // Merchant Tools
    public const string ListMerchantOrders = "list_merchant_orders";
    public const string GetMerchantIntegrationGuide = "get_merchant_integration_guide";

    // Admin Tools
    public const string GetDeliverySystemReports = "get_delivery_system_reports";
    public const string ListPricingTariffs = "list_pricing_tariffs";
    public const string GetAdminSystemOverview = "get_admin_system_overview";

    public static readonly IReadOnlyList<string> All =
    [
        TrackParcel,
        ListMyParcels,
        EstimateDeliveryFee,
        ListServiceZones,
        GetCourierAssignedDeliveries,
        GetCourierWorkSummary,
        GetCourierDutyStatus,
        GetCourierDeliveryProtocol,
        ListUnassignedParcels,
        GetCourierWorkload,
        GetZoneDispatchSummary,
        ListMerchantOrders,
        GetMerchantIntegrationGuide,
        GetDeliverySystemReports,
        ListPricingTariffs,
        GetAdminSystemOverview
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
    string? ParcelId = null,
    bool IsPublic = false);

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

// -------------------------------------------------------------
// Role-Specific Response Cards
// -------------------------------------------------------------

// Courier Cards
public sealed record CourierAssignmentItem(
    string TrackingNumber,
    string Status,
    string RecipientName,
    string RecipientPhone,
    string DropoffAddress,
    string DropoffCity,
    decimal WeightKg,
    string? ExternalOrderId = null);

public sealed record CourierAssignmentsCardData(
    int TotalCount,
    DateOnly ServiceDate,
    IReadOnlyList<CourierAssignmentItem> Assignments);

public sealed record CourierWorkSummaryCardData(
    DateOnly ServiceDate,
    int Assigned,
    int PickedUp,
    int InTransit,
    int OutForDelivery,
    int Delivered,
    int Failed,
    int Active,
    int Completed,
    int OverdueCount);

public sealed record CourierDutyCardData(
    bool OnDuty,
    string? VehicleType,
    string? VehicleNumber,
    decimal MaxWeightKg,
    string StatusMessage);

public sealed record CourierProtocolCardData(
    string Title,
    IReadOnlyList<string> Steps,
    string ExceptionGuidelines);

// Dispatcher Cards
public sealed record UnassignedParcelItem(
    Guid ParcelId,
    string TrackingNumber,
    string PickupCity,
    string DeliveryCity,
    decimal WeightKg,
    string Size,
    DateTimeOffset CreatedAt);

public sealed record DispatcherQueueCardData(
    int TotalUnassigned,
    IReadOnlyList<UnassignedParcelItem> Parcels);

public sealed record CourierWorkloadItem(
    Guid CourierId,
    string CourierCode,
    bool OnDuty,
    int ActiveCount,
    int Capacity,
    string VehicleType,
    decimal MaxWeightKg);

public sealed record CourierWorkloadCardData(
    int TotalCouriers,
    int ActiveCouriersOnDuty,
    IReadOnlyList<CourierWorkloadItem> Couriers);

public sealed record ZoneDispatchItem(
    string ZoneName,
    int PendingPickups,
    int InTransit,
    int CouriersCount);

public sealed record ZoneDispatchCardData(
    IReadOnlyList<ZoneDispatchItem> Zones);

// Merchant Cards
public sealed record MerchantOrderItem(
    string TrackingNumber,
    string? ExternalOrderId,
    string Status,
    string RecipientCity,
    decimal WeightKg,
    decimal? PriceLkr,
    DateTimeOffset CreatedAt);

public sealed record MerchantOrdersCardData(
    int TotalOrders,
    IReadOnlyList<MerchantOrderItem> Orders);

public sealed record MerchantIntegrationCardData(
    string ApiBaseUrl,
    string Endpoint,
    IReadOnlyList<string> WebhookEvents,
    string SignatureAlgorithm,
    string DocumentationUrl);

// Admin Cards
public sealed record AdminReportMetricItem(
    string Date,
    int Delivered,
    int Failed,
    int Cancelled);

public sealed record AdminReportCardData(
    DateTimeOffset From,
    DateTimeOffset To,
    int Delivered,
    int Failed,
    int Cancelled,
    int TotalTerminal,
    decimal SuccessRate,
    decimal FailureRate,
    IReadOnlyList<AdminReportMetricItem> DailyMetrics);

public sealed record AdminTariffItem(
    string ZoneName,
    string Size,
    decimal BaseRate,
    decimal PerKgRate,
    string Currency,
    bool IsActive,
    string PricingVersion);

public sealed record AdminTariffsCardData(
    int TotalTiers,
    IReadOnlyList<AdminTariffItem> Tiers);

public sealed record AdminOverviewCardData(
    int TotalParcelsToday,
    int UnassignedParcels,
    int ActiveCouriersOnDuty,
    int ActiveZonesCount,
    decimal TodaySuccessRate);

public sealed record AssistantCards(
    TrackingCardData? Tracking = null,
    ParcelListCardData? ParcelList = null,
    QuoteCardData? Quote = null,
    ZonesCardData? Zones = null,
    CourierAssignmentsCardData? CourierAssignments = null,
    CourierWorkSummaryCardData? CourierWorkSummary = null,
    CourierDutyCardData? CourierDuty = null,
    CourierProtocolCardData? CourierProtocol = null,
    DispatcherQueueCardData? DispatcherQueue = null,
    CourierWorkloadCardData? CourierWorkload = null,
    ZoneDispatchCardData? ZoneDispatch = null,
    MerchantOrdersCardData? MerchantOrders = null,
    MerchantIntegrationCardData? MerchantIntegration = null,
    AdminReportCardData? AdminReport = null,
    AdminTariffsCardData? AdminTariffs = null,
    AdminOverviewCardData? AdminOverview = null);

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
