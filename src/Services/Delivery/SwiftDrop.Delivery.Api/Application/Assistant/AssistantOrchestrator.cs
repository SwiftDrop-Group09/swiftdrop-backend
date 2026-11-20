using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace SwiftDrop.Delivery.Api.Application.Assistant;

public sealed partial class AssistantOrchestrator(
    AssistantToolExecutor toolExecutor,
    IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AssistantChatResponse> ProcessChatAsync(
        AssistantChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var rawMessage = request.Message?.Trim() ?? string.Empty;
        var role = toolExecutor.DeliveryUser.PrimaryRole;

        // 1. Guardrail / Injection Check
        if (IsGuardrailViolation(rawMessage, role, out var guardrailReason))
        {
            return new AssistantChatResponse(
                Reply: "⚠️ **Security Guardrail Triggered**\n\n" +
                       "Autonomous administrative actions, write operations (booking, courier assignment, status changes, refunds), " +
                       "and unauthorized cross-tenant data access are strictly prohibited.\n\n" +
                       "The SwiftDrop AI Delivery Assistant operates exclusively in read-only assistance mode. " +
                       "Please use the standard authenticated management portals for role-authorized administrative tasks.",
                Mode: "mcp-local-agent",
                ToolCalls: Array.Empty<AssistantToolCallTrace>(),
                Cards: null,
                SuggestedPrompts: GetSuggestedPromptsForRole(role),
                Guardrail: new GuardrailResult(Blocked: true, Reason: guardrailReason));
        }

        // 2. Pre-check explicit tracking numbers (e.g. TRK-..., SD-...) for deterministic, immediate tracking
        var trackingMatch = Regex.Match(rawMessage, @"\b((?:TRK|SD)-[A-Za-z0-9-]+)\b", RegexOptions.IgnoreCase);
        if (trackingMatch.Success)
        {
            return await ProcessWithBuiltInEngineAsync(request, cancellationToken);
        }

        // 3. Check if external LLM mode is configured and reachable
        var apiKey = configuration["AI:ApiKey"]
            ?? configuration["DELIVERY_AI_API_KEY"]
            ?? configuration["GEMINI_API_KEY"]
            ?? configuration["OPENAI_API_KEY"];

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var geminiResponse = await ProcessWithGeminiAsync(request, apiKey.Trim(), cancellationToken);
                if (geminiResponse != null)
                {
                    return geminiResponse;
                }
            }
            catch
            {
                // Fall back gracefully to built-in deterministic engine on transient error
            }
        }

        // 4. Built-In Resilient Deterministic Tool-Calling Engine
        return await ProcessWithBuiltInEngineAsync(request, cancellationToken);
    }

    private async Task<AssistantChatResponse> ProcessWithBuiltInEngineAsync(
        AssistantChatRequest request,
        CancellationToken cancellationToken)
    {
        var message = request.Message;
        var role = toolExecutor.DeliveryUser.PrimaryRole;
        var lower = message.ToLowerInvariant();
        var toolCalls = new List<AssistantToolCallTrace>();
        var cards = new AssistantCards();

        // -------------------------------------------------------------
        // Courier Role Intents
        // -------------------------------------------------------------
        if (role == "Courier")
        {
            if (Regex.IsMatch(lower, @"\b(assigned|assignments|today'?s deliveries|my route|manifest|what do i deliver|parcels for me)\b"))
            {
                var sw = Stopwatch.StartNew();
                var result = await toolExecutor.ExecuteAsync(AssistantToolNames.GetCourierAssignedDeliveries, null, cancellationToken);
                sw.Stop();
                toolCalls.Add(new AssistantToolCallTrace(AssistantToolNames.GetCourierAssignedDeliveries, new { }, result.Success ? "success" : "error", sw.ElapsedMilliseconds, result.TextSummary));
                return new AssistantChatResponse(result.TextSummary, "mcp-local-agent", toolCalls, new AssistantCards(CourierAssignments: result.Data as CourierAssignmentsCardData), GetSuggestedPromptsForRole(role));
            }

            if (Regex.IsMatch(lower, @"\b(summary|shift progress|how many did i deliver|overdue|work summary)\b"))
            {
                var sw = Stopwatch.StartNew();
                var result = await toolExecutor.ExecuteAsync(AssistantToolNames.GetCourierWorkSummary, null, cancellationToken);
                sw.Stop();
                toolCalls.Add(new AssistantToolCallTrace(AssistantToolNames.GetCourierWorkSummary, new { }, result.Success ? "success" : "error", sw.ElapsedMilliseconds, result.TextSummary));
                return new AssistantChatResponse(result.TextSummary, "mcp-local-agent", toolCalls, new AssistantCards(CourierWorkSummary: result.Data as CourierWorkSummaryCardData), GetSuggestedPromptsForRole(role));
            }

            if (Regex.IsMatch(lower, @"\b(duty|shift status|am i on duty|vehicle|capacity)\b"))
            {
                var sw = Stopwatch.StartNew();
                var result = await toolExecutor.ExecuteAsync(AssistantToolNames.GetCourierDutyStatus, null, cancellationToken);
                sw.Stop();
                toolCalls.Add(new AssistantToolCallTrace(AssistantToolNames.GetCourierDutyStatus, new { }, result.Success ? "success" : "error", sw.ElapsedMilliseconds, result.TextSummary));
                return new AssistantChatResponse(result.TextSummary, "mcp-local-agent", toolCalls, new AssistantCards(CourierDuty: result.Data as CourierDutyCardData), GetSuggestedPromptsForRole(role));
            }

            if (Regex.IsMatch(lower, @"\b(proof|signature|photo|protocol|sop|how to deliver|failed delivery|contactless)\b"))
            {
                var sw = Stopwatch.StartNew();
                var result = await toolExecutor.ExecuteAsync(AssistantToolNames.GetCourierDeliveryProtocol, null, cancellationToken);
                sw.Stop();
                toolCalls.Add(new AssistantToolCallTrace(AssistantToolNames.GetCourierDeliveryProtocol, new { }, result.Success ? "success" : "error", sw.ElapsedMilliseconds, result.TextSummary));
                return new AssistantChatResponse(result.TextSummary, "mcp-local-agent", toolCalls, new AssistantCards(CourierProtocol: result.Data as CourierProtocolCardData), GetSuggestedPromptsForRole(role));
            }
        }

        // -------------------------------------------------------------
        // Dispatcher Role Intents
        // -------------------------------------------------------------
        if (role is "Dispatcher" or "Admin")
        {
            if (Regex.IsMatch(lower, @"\b(unassigned|waiting parcels|dispatch queue|pending assignment|unassigned queue)\b"))
            {
                var sw = Stopwatch.StartNew();
                var result = await toolExecutor.ExecuteAsync(AssistantToolNames.ListUnassignedParcels, null, cancellationToken);
                sw.Stop();
                toolCalls.Add(new AssistantToolCallTrace(AssistantToolNames.ListUnassignedParcels, new { }, result.Success ? "success" : "error", sw.ElapsedMilliseconds, result.TextSummary));
                return new AssistantChatResponse(result.TextSummary, "mcp-local-agent", toolCalls, new AssistantCards(DispatcherQueue: result.Data as DispatcherQueueCardData), GetSuggestedPromptsForRole(role));
            }

            if (Regex.IsMatch(lower, @"\b(workload|couriers|courier capacity|who is on duty|active couriers)\b"))
            {
                var sw = Stopwatch.StartNew();
                var result = await toolExecutor.ExecuteAsync(AssistantToolNames.GetCourierWorkload, null, cancellationToken);
                sw.Stop();
                toolCalls.Add(new AssistantToolCallTrace(AssistantToolNames.GetCourierWorkload, new { }, result.Success ? "success" : "error", sw.ElapsedMilliseconds, result.TextSummary));
                return new AssistantChatResponse(result.TextSummary, "mcp-local-agent", toolCalls, new AssistantCards(CourierWorkload: result.Data as CourierWorkloadCardData), GetSuggestedPromptsForRole(role));
            }

            if (Regex.IsMatch(lower, @"\b(zone summary|bottlenecks?|dispatch summary|zone status)\b"))
            {
                var sw = Stopwatch.StartNew();
                var result = await toolExecutor.ExecuteAsync(AssistantToolNames.GetZoneDispatchSummary, null, cancellationToken);
                sw.Stop();
                toolCalls.Add(new AssistantToolCallTrace(AssistantToolNames.GetZoneDispatchSummary, new { }, result.Success ? "success" : "error", sw.ElapsedMilliseconds, result.TextSummary));
                return new AssistantChatResponse(result.TextSummary, "mcp-local-agent", toolCalls, new AssistantCards(ZoneDispatch: result.Data as ZoneDispatchCardData), GetSuggestedPromptsForRole(role));
            }
        }

        // -------------------------------------------------------------
        // Merchant Role Intents
        // -------------------------------------------------------------
        if (role is "Merchant" or "Admin")
        {
            if (Regex.IsMatch(lower, @"\b(my orders|merchant orders|business deliveries|commercial orders|recent orders)\b"))
            {
                var sw = Stopwatch.StartNew();
                var result = await toolExecutor.ExecuteAsync(AssistantToolNames.ListMerchantOrders, null, cancellationToken);
                sw.Stop();
                toolCalls.Add(new AssistantToolCallTrace(AssistantToolNames.ListMerchantOrders, new { }, result.Success ? "success" : "error", sw.ElapsedMilliseconds, result.TextSummary));
                return new AssistantChatResponse(result.TextSummary, "mcp-local-agent", toolCalls, new AssistantCards(MerchantOrders: result.Data as MerchantOrdersCardData), GetSuggestedPromptsForRole(role));
            }

            if (Regex.IsMatch(lower, @"\b(api|integration|webhooks?|api key|developer guide|signature|hmac)\b"))
            {
                var sw = Stopwatch.StartNew();
                var result = await toolExecutor.ExecuteAsync(AssistantToolNames.GetMerchantIntegrationGuide, null, cancellationToken);
                sw.Stop();
                toolCalls.Add(new AssistantToolCallTrace(AssistantToolNames.GetMerchantIntegrationGuide, new { }, result.Success ? "success" : "error", sw.ElapsedMilliseconds, result.TextSummary));
                return new AssistantChatResponse(result.TextSummary, "mcp-local-agent", toolCalls, new AssistantCards(MerchantIntegration: result.Data as MerchantIntegrationCardData), GetSuggestedPromptsForRole(role));
            }
        }

        // -------------------------------------------------------------
        // Admin Role Intents
        // -------------------------------------------------------------
        if (role == "Admin")
        {
            if (Regex.IsMatch(lower, @"\b(reports?|kpis?|metrics?|delivery volume|success rate|performance)\b"))
            {
                var sw = Stopwatch.StartNew();
                var result = await toolExecutor.ExecuteAsync(AssistantToolNames.GetDeliverySystemReports, null, cancellationToken);
                sw.Stop();
                toolCalls.Add(new AssistantToolCallTrace(AssistantToolNames.GetDeliverySystemReports, new { }, result.Success ? "success" : "error", sw.ElapsedMilliseconds, result.TextSummary));
                return new AssistantChatResponse(result.TextSummary, "mcp-local-agent", toolCalls, new AssistantCards(AdminReport: result.Data as AdminReportCardData), GetSuggestedPromptsForRole(role));
            }

            if (Regex.IsMatch(lower, @"\b(tariffs?|pricing tiers?|rates schedule|admin pricing)\b"))
            {
                var sw = Stopwatch.StartNew();
                var result = await toolExecutor.ExecuteAsync(AssistantToolNames.ListPricingTariffs, null, cancellationToken);
                sw.Stop();
                toolCalls.Add(new AssistantToolCallTrace(AssistantToolNames.ListPricingTariffs, new { }, result.Success ? "success" : "error", sw.ElapsedMilliseconds, result.TextSummary));
                return new AssistantChatResponse(result.TextSummary, "mcp-local-agent", toolCalls, new AssistantCards(AdminTariffs: result.Data as AdminTariffsCardData), GetSuggestedPromptsForRole(role));
            }

            if (Regex.IsMatch(lower, @"\b(system overview|admin overview|platform status|health)\b"))
            {
                var sw = Stopwatch.StartNew();
                var result = await toolExecutor.ExecuteAsync(AssistantToolNames.GetAdminSystemOverview, null, cancellationToken);
                sw.Stop();
                toolCalls.Add(new AssistantToolCallTrace(AssistantToolNames.GetAdminSystemOverview, new { }, result.Success ? "success" : "error", sw.ElapsedMilliseconds, result.TextSummary));
                return new AssistantChatResponse(result.TextSummary, "mcp-local-agent", toolCalls, new AssistantCards(AdminOverview: result.Data as AdminOverviewCardData), GetSuggestedPromptsForRole(role));
            }
        }

        // 1. Fee Estimation Intent (e.g. "How much to send 2 kg to Kandy?", "Cost of 5kg parcel from Colombo to Galle")
        if (IsFeeEstimateIntent(message, request.History, out var feeArgs, out var needsPickupCity))
        {
            if (needsPickupCity)
            {
                return new AssistantChatResponse(
                    Reply: $"Where will the parcel be picked up from? Please provide your starting position (pickup city) to calculate an accurate delivery fee to {feeArgs.DeliveryCity ?? "your destination"} using our active pricing rates.",
                    Mode: "mcp-local-agent",
                    ToolCalls: Array.Empty<AssistantToolCallTrace>(),
                    Cards: null,
                    SuggestedPrompts: null);
            }

            var sw = Stopwatch.StartNew();
            var jsonArgs = JsonSerializer.SerializeToElement(feeArgs, JsonOptions);
            var result = await toolExecutor.ExecuteAsync(AssistantToolNames.EstimateDeliveryFee, jsonArgs, cancellationToken);
            sw.Stop();

            toolCalls.Add(new AssistantToolCallTrace(
                AssistantToolNames.EstimateDeliveryFee,
                feeArgs,
                result.Success ? "success" : "error",
                sw.ElapsedMilliseconds,
                result.TextSummary));

            QuoteCardData? quoteCard = null;
            if (result.Success && result.Data is QuoteCardData qc)
            {
                quoteCard = qc;
            }

            var reply = result.TextSummary;

            return new AssistantChatResponse(
                Reply: reply,
                Mode: "mcp-local-agent",
                ToolCalls: toolCalls,
                Cards: new AssistantCards(Quote: quoteCard),
                SuggestedPrompts: GetSuggestedPromptsForRole(role));
        }

        // 2. Tracking Intent (e.g. "Where is my parcel?", "Track SD-2026-0001", "Status of SD-1234")
        if (IsTrackingIntent(message, out var trackingArgs))
        {
            var sw = Stopwatch.StartNew();
            var jsonArgs = JsonSerializer.SerializeToElement(trackingArgs, JsonOptions);
            var result = await toolExecutor.ExecuteAsync(AssistantToolNames.TrackParcel, jsonArgs, cancellationToken);
            sw.Stop();

            toolCalls.Add(new AssistantToolCallTrace(
                AssistantToolNames.TrackParcel,
                trackingArgs,
                result.Success ? "success" : "error",
                sw.ElapsedMilliseconds,
                result.TextSummary));

            TrackingCardData? trackingCard = null;
            ParcelListCardData? listCard = null;

            if (result.Success && result.Data is TrackingCardData directTc)
            {
                trackingCard = directTc;
            }
            else if (result.Success && result.Data is not null)
            {
                var json = JsonSerializer.Serialize(result.Data, JsonOptions);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("trackingNumber", out var tnProp) && root.TryGetProperty("status", out var stProp))
                {
                    var tn = tnProp.GetString() ?? "";
                    var status = stProp.GetString() ?? "";
                    var origin = root.TryGetProperty("pickupCity", out var oc) ? oc.GetString() : root.TryGetProperty("originCity", out var orig) ? orig.GetString() : "Colombo";
                    var dest = root.TryGetProperty("deliveryCity", out var dc) ? dc.GetString() : root.TryGetProperty("destinationCity", out var dst) ? dst.GetString() : "Kandy";
                    var hasReceipt = root.TryGetProperty("hasReceipt", out var hr) && hr.GetBoolean();
                    var isPub = root.TryGetProperty("isPublic", out var ip) && ip.GetBoolean();
                    var pId = root.TryGetProperty("parcelId", out var pi) ? pi.GetString() : null;

                    var steps = new List<MilestoneStep>();
                    if (root.TryGetProperty("milestones", out var ms) && ms.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in ms.EnumerateArray())
                        {
                            var sName = m.GetProperty("status").GetString() ?? "";
                            var label = m.GetProperty("label").GetString() ?? "";
                            var completed = m.GetProperty("completed").GetBoolean();
                            var current = m.GetProperty("current").GetBoolean();
                            DateTimeOffset? ts = m.TryGetProperty("timestamp", out var tProp) && tProp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(tProp.GetString(), out var parsedTs) ? parsedTs : null;
                            steps.Add(new MilestoneStep(sName, label, ts, completed, current));
                        }
                    }

                    trackingCard = new TrackingCardData(
                        tn,
                        status,
                        origin ?? "Origin",
                        dest ?? "Destination",
                        DateTimeOffset.UtcNow,
                        steps,
                        hasReceipt,
                        pId,
                        isPub);
                }
            }

            var reply = result.TextSummary;

            return new AssistantChatResponse(
                Reply: reply,
                Mode: "mcp-local-agent",
                ToolCalls: toolCalls,
                Cards: new AssistantCards(Tracking: trackingCard, ParcelList: listCard),
                SuggestedPrompts: GetSuggestedPromptsForRole(role));
        }

        // 3. Parcel List Intent (e.g. "List my deliveries", "Show my parcels", "Recent orders")
        if (IsListParcelsIntent(message, out var listArgs))
        {
            var sw = Stopwatch.StartNew();
            var jsonArgs = JsonSerializer.SerializeToElement(listArgs, JsonOptions);
            var result = await toolExecutor.ExecuteAsync(AssistantToolNames.ListMyParcels, jsonArgs, cancellationToken);
            sw.Stop();

            toolCalls.Add(new AssistantToolCallTrace(
                AssistantToolNames.ListMyParcels,
                listArgs,
                result.Success ? "success" : "error",
                sw.ElapsedMilliseconds,
                result.TextSummary));

            ParcelListCardData? listCard = result.Data as ParcelListCardData;

            return new AssistantChatResponse(
                Reply: result.TextSummary,
                Mode: "mcp-local-agent",
                ToolCalls: toolCalls,
                Cards: new AssistantCards(ParcelList: listCard),
                SuggestedPrompts: GetSuggestedPromptsForRole(role));
        }

        // 4. Zone Coverage Intent (e.g. "What zones do you cover?", "Where do you deliver?", "Service area")
        if (IsZonesIntent(message))
        {
            var sw = Stopwatch.StartNew();
            var result = await toolExecutor.ExecuteAsync(AssistantToolNames.ListServiceZones, null, cancellationToken);
            sw.Stop();

            toolCalls.Add(new AssistantToolCallTrace(
                AssistantToolNames.ListServiceZones,
                new { },
                result.Success ? "success" : "error",
                sw.ElapsedMilliseconds,
                result.TextSummary));

            ZonesCardData? zonesCard = result.Data as ZonesCardData;

            return new AssistantChatResponse(
                Reply: result.TextSummary,
                Mode: "mcp-local-agent",
                ToolCalls: toolCalls,
                Cards: new AssistantCards(Zones: zonesCard),
                SuggestedPrompts: GetSuggestedPromptsForRole(role));
        }

        // 5. General Greeting vs Off-Topic Handling
        var isGreeting = string.IsNullOrWhiteSpace(message) || Regex.IsMatch(lower, @"\b(hi|hello|hey|good (morning|afternoon|evening)|greetings|start|help)\b");

        var (welcomeGreeting, offTopicMsg) = GetWelcomeMessagesForRole(role);
        var replyText = isGreeting ? welcomeGreeting : offTopicMsg;

        return new AssistantChatResponse(
            Reply: replyText,
            Mode: "mcp-local-agent",
            ToolCalls: Array.Empty<AssistantToolCallTrace>(),
            Cards: null,
            SuggestedPrompts: GetSuggestedPromptsForRole(role));
    }

    // -------------------------------------------------------------
    // Intent Recognizers & Security Guardrails
    // -------------------------------------------------------------

    public static IReadOnlyList<string> GetSuggestedPromptsForRole(string role) => role switch
    {
        "Courier" =>
        [
            "What are my assigned deliveries today?",
            "What is my current work summary?",
            "What is my duty status?",
            "How do I complete proof of delivery?"
        ],
        "Dispatcher" =>
        [
            "How many unassigned parcels are waiting?",
            "Show courier workload across zones",
            "What is the zone summary for dispatch?",
            "What delivery zones do you cover?"
        ],
        "Merchant" =>
        [
            "Show my recent merchant orders",
            "How do I integrate via API and webhooks?",
            "How much to send 5 kg from Colombo to Kandy?",
            "What delivery zones do you cover?"
        ],
        "Admin" =>
        [
            "Show delivery system reports and KPIs",
            "What are the active pricing tiers?",
            "How many unassigned parcels are waiting?",
            "Show system operational overview"
        ],
        _ =>
        [
            "Where is my parcel?",
            "How much to send 2 kg to Kandy?",
            "What delivery zones do you cover?"
        ]
    };

    private static (string Greeting, string OffTopic) GetWelcomeMessagesForRole(string role) => role switch
    {
        "Courier" => (
            "Hello! I am your SwiftDrop Courier Assistant. I can help you check your assigned deliveries for today, review shift workload and duty status, and guide you through delivery proof procedures.",
            "I'm the SwiftDrop Courier Assistant. I can help with your assigned deliveries, shift summary, duty status, and delivery proof guidelines. Let me know what you need assistance with!"
        ),
        "Dispatcher" => (
            "Hello! I am your SwiftDrop Dispatch Operations Assistant. I can help you monitor unassigned parcel queues, assess courier workload and zone capacities, and inspect delivery bottlenecks across Sri Lanka.",
            "I'm the SwiftDrop Dispatch Assistant. I can help with unassigned queues, courier workload, zone capacity, and operational parcel tracking. Let me know what you'd like to inspect!"
        ),
        "Merchant" => (
            "Hello! I am your SwiftDrop Merchant Assistant. I can help you track your business orders, calculate commercial shipping rates, and guide your team on API integration and webhook events.",
            "I'm the SwiftDrop Merchant Assistant. I can help with your merchant orders, commercial shipping quotes, and developer API/webhook integration. Let me know how I can assist your business!"
        ),
        "Admin" => (
            "Hello! I am your SwiftDrop Administrative Assistant. I can help you analyze system delivery reports and KPIs, audit pricing tariffs, and review zone operations.",
            "I'm the SwiftDrop Admin Assistant. I can help with delivery reports, KPI metrics, pricing tariffs, and operational overviews. What would you like to review?"
        ),
        _ => (
            "Hello! I am your SwiftDrop Delivery Assistant. I can help you track parcels, calculate shipping rates, and check service zones across Sri Lanka. How can I help you today?",
            "I'm the SwiftDrop Delivery Assistant, so I can only help with parcel tracking, shipping rate estimates, and delivery coverage across Sri Lanka. Let me know if you'd like to track a parcel or calculate delivery fees!"
        )
    };

    private static bool IsGuardrailViolation(string text, string role, out string reason)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            reason = string.Empty;
            return false;
        }

        var lower = text.ToLowerInvariant();

        // 1. Admin escalation & prompt injection
        if (Regex.IsMatch(lower, @"\b(act as admin|become admin|admin role|grant.*role|system prompt|ignore (all )?previous instructions|override.*instructions|jailbreak|sql injection|drop table|truncate)\b"))
        {
            reason = "Unauthorized administrative privilege escalation attempt.";
            return true;
        }

        // 2. Autonomous writes
        if (Regex.IsMatch(lower, @"\b(book a parcel for me|confirm booking autonomously|assign courier|change status|mark delivered|mark failed|issue refund|cancel parcel|delete user)\b"))
        {
            reason = "Autonomous database write operations are prohibited. Bookings and assignments must be confirmed through the official user interface.";
            return true;
        }

        // 3. Cross-tenant inspection for Customer / Guest
        if (role is "Customer" or "Guest" && Regex.IsMatch(lower, @"\b(all customers|other users|show all parcels|other customer's|dump database|dump all)\b"))
        {
            reason = "Cross-tenant data exposure request blocked by server security policy.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool IsFeeEstimateIntent(
        string text,
        IReadOnlyList<ChatMessage>? history,
        out EstimateFeeInput args,
        out bool needsPickupCity)
    {
        var lower = text.ToLowerInvariant();
        var hasPricingKeyword = Regex.IsMatch(lower, @"\b(how much|cost|price|fee|rate|quote|estimate|send.*to|send.*kg|how about|what about)\b");
        var hasRoute = Regex.IsMatch(lower, @"\bfrom\s+[a-z]+\s+to\s+[a-z]+\b");

        // Check if there is existing fee/quote discussion in conversation history
        var hasHistoryQuote = history != null && history.Any(h =>
            h.Content.Contains("costs LKR", StringComparison.OrdinalIgnoreCase) ||
            h.Content.Contains("quote is locked", StringComparison.OrdinalIgnoreCase) ||
            h.Content.Contains("delivery fee", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(h.Content, @"\b(how much|shipping rate|calculate delivery fee)\b", RegexOptions.IgnoreCase));

        // Check if the user is replying with a pickup city after the assistant asked for one
        var lastAssistantAskedPickup = history != null && history.Count > 0 &&
            history.LastOrDefault(h => h.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) || h.Role.Equals("model", StringComparison.OrdinalIgnoreCase)) is { } lastAssistant &&
            (lastAssistant.Content.Contains("starting position", StringComparison.OrdinalIgnoreCase) || lastAssistant.Content.Contains("picked up from", StringComparison.OrdinalIgnoreCase));

        // If history had a quote or fee inquiry, a follow-up mentioning a city, "how about", "what about", "for ...", "if ...", weight or size is a fee intent
        var isFollowUpInquiry = hasHistoryQuote && (
            lower.StartsWith("how about") ||
            lower.StartsWith("what about") ||
            lower.StartsWith("and ") ||
            lower.StartsWith("for ") ||
            lower.StartsWith("if ") ||
            lower.Contains("possible to send") ||
            Regex.IsMatch(lower, @"\b(from|to)\s+[a-z]+\b") ||
            Regex.IsMatch(lower, @"\b\d+(\.\d+)?\s*(kg|kilo|k|kn)?\b") ||
            Regex.IsMatch(lower, @"\b(small|medium|large|medim|medum|smal|sml|larg|lrg)\b"));

        if (!hasPricingKeyword && !hasRoute && !lastAssistantAskedPickup && !isFollowUpInquiry)
        {
            args = new EstimateFeeInput();
            needsPickupCity = false;
            return false;
        }

        // 1. Resolve weight (check current message first, including typos like 25kn, 25k, or standalone digits after follow-up words)
        decimal weight = 2.0m;
        var weightMatch = Regex.Match(lower, @"\b(\d+(?:\.\d+)?)\s*(?:kg|kilo(?:gram)?s?|k|kn|kgs|kilos)\b");
        if (!weightMatch.Success)
        {
            weightMatch = Regex.Match(lower, @"\b(?:how about|what about|if|for|send|parcel of|a)\s+(\d+(?:\.\d+)?)\b");
        }

        if (weightMatch.Success && decimal.TryParse(weightMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var parsedWeight) && parsedWeight > 0)
        {
            weight = parsedWeight;
        }
        else if (history != null)
        {
            for (var i = history.Count - 1; i >= 0; i--)
            {
                var hLower = history[i].Content.ToLowerInvariant();
                var hWeight = Regex.Match(hLower, @"\b(\d+(?:\.\d+)?)\s*(?:kg|kilo(?:gram)?s?|k|kn|kgs|kilos)\b");
                if (hWeight.Success && decimal.TryParse(hWeight.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var prevW) && prevW > 0)
                {
                    weight = prevW;
                    break;
                }
            }
        }

        // 2. Resolve pickup city (starting position)
        string? pickupCity = null;
        var fromMatch = Regex.Match(text, @"\bfrom\s+([A-Za-z]+)", RegexOptions.IgnoreCase);
        if (fromMatch.Success)
        {
            pickupCity = fromMatch.Groups[1].Value;
        }
        else if (lastAssistantAskedPickup)
        {
            var trimmed = text.Trim().Trim('.', '!', '?');
            var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= 4)
            {
                var cleanCity = Regex.Replace(trimmed, @"^(from|in|at)\s+", "", RegexOptions.IgnoreCase).Trim();
                if (!string.IsNullOrWhiteSpace(cleanCity))
                {
                    pickupCity = cleanCity;
                }
            }
        }

        // 3. Resolve delivery city (destination)
        string? deliveryCity = null;
        var toMatches = Regex.Matches(text, @"\b(?:to|into)\s+(?!send\b|ship\b|deliver\b|post\b|calculate\b|check\b|track\b|book\b|proceed\b|pay\b|view\b|get\b|see\b|find\b|use\b|order\b|help\b|verify\b)([A-Za-z]+)", RegexOptions.IgnoreCase);
        if (toMatches.Count > 0)
        {
            deliveryCity = toMatches[^1].Groups[1].Value;
        }
        else
        {
            var knownCities = new[] { "Kandy", "Colombo", "Galle", "Negombo", "Jaffna", "Kurunegala", "Matara", "Gampaha", "Moratuwa", "Batticaloa", "Trincomalee" };
            foreach (var city in knownCities)
            {
                if (lower.Contains(city.ToLowerInvariant()))
                {
                    deliveryCity = city;
                    break;
                }
            }
        }

        // If either city is still unresolved, extract from conversation history
        if ((pickupCity == null || deliveryCity == null) && hasHistoryQuote && history != null)
        {
            var (histPickup, histDelivery) = ExtractRouteFromHistory(history);
            pickupCity ??= histPickup;
            deliveryCity ??= histDelivery;
        }

        // Extract size (handling typos like medim, smal, etc.)
        string? size = null;
        if (Regex.IsMatch(lower, @"\b(sma?ll?|sml)\b")) size = "Small";
        else if (Regex.IsMatch(lower, @"\b(medi?u?m|med)\b")) size = "Medium";
        else if (Regex.IsMatch(lower, @"\b(la?rge?|lrg)\b")) size = "Large";
        else if (history != null)
        {
            for (var i = history.Count - 1; i >= 0; i--)
            {
                var hLower = history[i].Content.ToLowerInvariant();
                if (Regex.IsMatch(hLower, @"\b(sma?ll?|sml)\b")) { size = "Small"; break; }
                if (Regex.IsMatch(hLower, @"\b(medi?u?m|med)\b")) { size = "Medium"; break; }
                if (Regex.IsMatch(hLower, @"\b(la?rge?|lrg)\b")) { size = "Large"; break; }
            }
        }

        deliveryCity ??= "Kandy";

        if (string.IsNullOrWhiteSpace(pickupCity))
        {
            needsPickupCity = true;
            args = new EstimateFeeInput(
                WeightKg: weight,
                Size: size,
                PickupCity: null,
                DeliveryCity: deliveryCity);
            return true;
        }

        needsPickupCity = false;
        args = new EstimateFeeInput(
            WeightKg: weight,
            Size: size,
            PickupCity: pickupCity,
            DeliveryCity: deliveryCity);
        return true;
    }

    private static (string? Pickup, string? Delivery) ExtractRouteFromHistory(IReadOnlyList<ChatMessage>? history)
    {
        if (history == null) return (null, null);

        string? pickup = null;
        string? delivery = null;

        for (var i = history.Count - 1; i >= 0; i--)
        {
            var text = history[i].Content;

            // 1. Explicit pair: "from Colombo to Kandy" or "from **Colombo** to **Kandy**"
            var pairMatch = Regex.Match(text, @"\bfrom\s+\*?\*?([A-Za-z]+)\*?\*?\s+to\s+\*?\*?([A-Za-z]+)\*?\*?", RegexOptions.IgnoreCase);
            if (pairMatch.Success)
            {
                pickup ??= pairMatch.Groups[1].Value;
                delivery ??= pairMatch.Groups[2].Value;
                return (pickup, delivery);
            }

            // 2. Arrow notation: "Colombo → Kandy"
            var arrowMatch = Regex.Match(text, @"\b([A-Za-z]+)\s*(?:→|->)\s*([A-Za-z]+)\b", RegexOptions.IgnoreCase);
            if (arrowMatch.Success)
            {
                pickup ??= arrowMatch.Groups[1].Value;
                delivery ??= arrowMatch.Groups[2].Value;
                return (pickup, delivery);
            }

            // 3. Deep link URL parameters: "pickupCity=Colombo&deliveryCity=Kandy"
            var urlPickup = Regex.Match(text, @"pickupCity=([A-Za-z]+)", RegexOptions.IgnoreCase);
            var urlDelivery = Regex.Match(text, @"deliveryCity=([A-Za-z]+)", RegexOptions.IgnoreCase);
            if (urlPickup.Success || urlDelivery.Success)
            {
                pickup ??= urlPickup.Success ? urlPickup.Groups[1].Value : null;
                delivery ??= urlDelivery.Success ? urlDelivery.Groups[1].Value : null;
                if (pickup != null && delivery != null)
                {
                    return (pickup, delivery);
                }
            }

            // 4. In user messages only, look for "from <City>" and "to <City>"
            if (history[i].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                if (pickup == null)
                {
                    var uFrom = Regex.Match(text, @"\bfrom\s+([A-Za-z]+)", RegexOptions.IgnoreCase);
                    if (uFrom.Success) pickup = uFrom.Groups[1].Value;
                }

                if (delivery == null)
                {
                    var uTo = Regex.Matches(text, @"\b(?:to|into)\s+(?!send\b|ship\b|deliver\b|post\b|calculate\b|check\b|track\b|book\b|proceed\b|pay\b|view\b|get\b|see\b|find\b|use\b|order\b|help\b|verify\b)([A-Za-z]+)", RegexOptions.IgnoreCase);
                    if (uTo.Count > 0) delivery = uTo[^1].Groups[1].Value;
                }

                if (pickup != null && delivery != null)
                {
                    return (pickup, delivery);
                }
            }
        }

        return (pickup, delivery);
    }

    private static bool IsTrackingIntent(string text, out TrackParcelInput args)
    {
        var trackingMatch = Regex.Match(text, @"\b((?:TRK|SD)-[A-Za-z0-9-]+)\b", RegexOptions.IgnoreCase);
        if (trackingMatch.Success)
        {
            args = new TrackParcelInput(TrackingNumber: trackingMatch.Groups[1].Value.ToUpperInvariant());
            return true;
        }

        var lower = text.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"\b(where is my parcel|track|status of|where is delivery|track parcel|when is my delivery going to come)\b"))
        {
            args = new TrackParcelInput();
            return true;
        }

        args = new TrackParcelInput();
        return false;
    }

    private static bool IsListParcelsIntent(string text, out ListMyParcelsInput args)
    {
        var lower = text.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"\b(list.*parcels|my parcels|my deliveries|recent deliveries|show my shipments|all my parcels)\b"))
        {
            args = new ListMyParcelsInput(Limit: 5);
            return true;
        }

        args = new ListMyParcelsInput();
        return false;
    }

    private static bool IsZonesIntent(string text)
    {
        var lower = text.ToLowerInvariant();
        return Regex.IsMatch(lower, @"\b(zones|coverage|where do you deliver|service area|supported cities|cities you cover)\b");
    }

    private static readonly HttpClient GeminiClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private async Task<AssistantChatResponse?> ProcessWithGeminiAsync(
        AssistantChatRequest request,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var model = configuration["GEMINI_MODEL"] ?? "gemini-2.5-flash";
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        var contentsList = new List<object>();
        if (request.History != null)
        {
            foreach (var h in request.History)
            {
                if (!string.IsNullOrWhiteSpace(h.Content))
                {
                    var chatRole = h.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) || h.Role.Equals("model", StringComparison.OrdinalIgnoreCase)
                        ? "model"
                        : "user";
                    contentsList.Add(new
                    {
                        role = chatRole,
                        parts = new object[] { new { text = h.Content } }
                    });
                }
            }
        }

        contentsList.Add(new
        {
            role = "user",
            parts = new object[] { new { text = request.Message } }
        });

        var role = toolExecutor.DeliveryUser.PrimaryRole;
        var systemInstruction = new
        {
            parts = new object[]
            {
                new { text = $"You are the SwiftDrop AI Delivery Assistant, helping users in Sri Lanka. The authenticated user's role is: '{role}'. " +
                             "CRITICAL RULES: " +
                             "1. Role-Aware Capabilities: " +
                             "   - Customer: personal tracking, fee quotes, service zones, personal delivery history. " +
                             "   - Courier: assigned deliveries for today, shift work summary, duty status, and proof of delivery SOP protocol. " +
                             "   - Dispatcher: unassigned parcel queue, courier workload and fleet capacity across zones, and zone bottleneck summaries. " +
                             "   - Merchant: commercial order fulfillment, REST API integration, API keys, and webhook events. " +
                             "   - Admin: system delivery performance reports, pricing tariffs, and platform operational overview. " +
                             "   - Guest: public parcel tracking and zone fee estimates. " +
                             "2. Conversational Context & Memory: Maintain conversation context across turns. When a user asks a follow-up query like 'How about from Kandy to Jaffna', 'What about to Galle', or 'How much for 5kg', inherit any missing details from previous turns. " +
                             "3. Typo Resilience: Recognize common typos and phonetic variants of Sri Lankan city names (e.g. 'jafna' -> Jaffna, 'colambo' -> Colombo, 'kandi' -> Kandy, 'rathnapura' -> Ratnapura, 'trinco' -> Trincomalee). " +
                             "4. Delivery Fee Inquiries: When a user asks for shipping fees or quotes without providing BOTH starting position (pickup city) and destination (delivery city), ask them for their starting position (pickup city) first before calculating. Never assume Colombo or any default origin if not stated or inherited. Once both are known, ALWAYS invoke estimate_delivery_fee tool. " +
                             "5. Security: Autonomous write operations (booking, status changes, refunds, courier assignments) are strictly prohibited. Format currency as LKR. Keep answers concise, helpful, and friendly." }
            }
        };

        var toolsDeclaration = new object[]
        {
            new
            {
                function_declarations = new object[]
                {
                    new
                    {
                        name = AssistantToolNames.EstimateDeliveryFee,
                        description = "Estimates delivery fee based on package weight in kg, pickup city (starting position), and delivery city (destination) in Sri Lanka. Both pickup and delivery cities are required.",
                        parameters = new
                        {
                            type = "OBJECT",
                            properties = new Dictionary<string, object>
                            {
                                ["weightKg"] = new { type = "NUMBER", description = "Weight in kilograms (e.g. 2.0)" },
                                ["pickupCity"] = new { type = "STRING", description = "Starting position / pickup city in Sri Lanka (e.g. Colombo, Kandy, Galle, Moratuwa, Jaffna). Required." },
                                ["deliveryCity"] = new { type = "STRING", description = "Destination city in Sri Lanka (e.g. Kandy, Galle, Jaffna, Colombo). Required." },
                                ["size"] = new { type = "STRING", @enum = new[] { "Small", "Medium", "Large", "ExtraLarge" }, description = "Package size tier" }
                            },
                            required = new[] { "weightKg", "pickupCity", "deliveryCity" }
                        }
                    },
                    new
                    {
                        name = AssistantToolNames.TrackParcel,
                        description = "Tracks parcel delivery status, location, and milestone timeline by tracking number (e.g. TRK-..., SD-...).",
                        parameters = new
                        {
                            type = "OBJECT",
                            properties = new Dictionary<string, object>
                            {
                                ["trackingNumber"] = new { type = "STRING", description = "Tracking number" }
                            }
                        }
                    },
                    new
                    {
                        name = AssistantToolNames.ListMyParcels,
                        description = "Lists active and recent parcels for the authenticated customer.",
                        parameters = new
                        {
                            type = "OBJECT",
                            properties = new Dictionary<string, object>
                            {
                                ["limit"] = new { type = "INTEGER", description = "Max parcels to return (1-10)" }
                            }
                        }
                    },
                    new
                    {
                        name = AssistantToolNames.ListServiceZones,
                        description = "Lists active bookable delivery zones, supported cities, and postal codes in Sri Lanka.",
                        parameters = new
                        {
                            type = "OBJECT",
                            properties = new Dictionary<string, object>()
                        }
                    },
                    new
                    {
                        name = AssistantToolNames.GetCourierAssignedDeliveries,
                        description = "Retrieves today's assigned deliveries for the authenticated courier.",
                        parameters = new { type = "OBJECT", properties = new Dictionary<string, object>() }
                    },
                    new
                    {
                        name = AssistantToolNames.GetCourierWorkSummary,
                        description = "Retrieves shift summary and counts for the authenticated courier.",
                        parameters = new { type = "OBJECT", properties = new Dictionary<string, object>() }
                    },
                    new
                    {
                        name = AssistantToolNames.GetCourierDutyStatus,
                        description = "Retrieves on-duty / off-duty status and vehicle capacity for the authenticated courier.",
                        parameters = new { type = "OBJECT", properties = new Dictionary<string, object>() }
                    },
                    new
                    {
                        name = AssistantToolNames.GetCourierDeliveryProtocol,
                        description = "Retrieves the proof of delivery procedure and guidelines for couriers.",
                        parameters = new { type = "OBJECT", properties = new Dictionary<string, object>() }
                    },
                    new
                    {
                        name = AssistantToolNames.ListUnassignedParcels,
                        description = "Lists unassigned parcels waiting in the dispatch queue.",
                        parameters = new { type = "OBJECT", properties = new Dictionary<string, object>() }
                    },
                    new
                    {
                        name = AssistantToolNames.GetCourierWorkload,
                        description = "Retrieves courier fleet workload and capacity across zones.",
                        parameters = new { type = "OBJECT", properties = new Dictionary<string, object>() }
                    },
                    new
                    {
                        name = AssistantToolNames.GetZoneDispatchSummary,
                        description = "Retrieves delivery density and courier coverage summarized by zone.",
                        parameters = new { type = "OBJECT", properties = new Dictionary<string, object>() }
                    },
                    new
                    {
                        name = AssistantToolNames.ListMerchantOrders,
                        description = "Lists commercial parcels and fulfillment statuses for the merchant.",
                        parameters = new { type = "OBJECT", properties = new Dictionary<string, object>() }
                    },
                    new
                    {
                        name = AssistantToolNames.GetMerchantIntegrationGuide,
                        description = "Retrieves REST API endpoints, API keys, and webhook integration guide for merchants.",
                        parameters = new { type = "OBJECT", properties = new Dictionary<string, object>() }
                    },
                    new
                    {
                        name = AssistantToolNames.GetDeliverySystemReports,
                        description = "Retrieves system-wide delivery performance reports and KPIs for administrators.",
                        parameters = new { type = "OBJECT", properties = new Dictionary<string, object>() }
                    },
                    new
                    {
                        name = AssistantToolNames.ListPricingTariffs,
                        description = "Lists active pricing tariffs and rates across delivery zones for administrators.",
                        parameters = new { type = "OBJECT", properties = new Dictionary<string, object>() }
                    },
                    new
                    {
                        name = AssistantToolNames.GetAdminSystemOverview,
                        description = "Retrieves high-level platform status and operational KPIs for administrators.",
                        parameters = new { type = "OBJECT", properties = new Dictionary<string, object>() }
                    }
                }
            }
        };

        var requestBody = new
        {
            contents = contentsList,
            system_instruction = systemInstruction,
            tools = toolsDeclaration
        };

        HttpResponseMessage response;
        try
        {
            response = await GeminiClient.PostAsJsonAsync(endpoint, requestBody, JsonOptions, cancellationToken);
            if (!response.IsSuccessStatusCode && !model.Contains("flash-lite", StringComparison.OrdinalIgnoreCase))
            {
                // Fallback to flash-lite if primary model returned 429 quota exhaustion or transient error
                var liteEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-lite:generateContent?key={apiKey}";
                var retryResp = await GeminiClient.PostAsJsonAsync(liteEndpoint, requestBody, JsonOptions, cancellationToken);
                if (retryResp.IsSuccessStatusCode)
                {
                    response = retryResp;
                    model = "gemini-2.5-flash-lite";
                    endpoint = liteEndpoint;
                }
            }
        }
        catch
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = doc.RootElement;
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            return null;
        }

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts) || parts.GetArrayLength() == 0)
        {
            return null;
        }

        var toolCalls = new List<AssistantToolCallTrace>();
        var cards = new AssistantCards();
        var replyText = string.Empty;

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var textProp))
            {
                replyText += textProp.GetString() + "\n";
            }
            else if (part.TryGetProperty("functionCall", out var fc))
            {
                var toolName = fc.GetProperty("name").GetString() ?? "";
                var rawArgsText = fc.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
                    ? a.GetRawText()
                    : "{}";

                object? argsObj;
                try
                {
                    argsObj = JsonSerializer.Deserialize<Dictionary<string, object?>>(rawArgsText, JsonOptions);
                }
                catch
                {
                    argsObj = new Dictionary<string, object?>();
                }

                var sw = Stopwatch.StartNew();
                using var argsDoc = JsonDocument.Parse(rawArgsText);
                var result = await toolExecutor.ExecuteAsync(toolName, argsDoc.RootElement, cancellationToken);
                sw.Stop();

                toolCalls.Add(new AssistantToolCallTrace(
                    toolName,
                    argsObj,
                    result.Success ? "success" : "error",
                    sw.ElapsedMilliseconds,
                    result.TextSummary));

                if (result.Success && result.Data is not null)
                {
                    if (result.Data is QuoteCardData qc) cards = cards with { Quote = qc };
                    else if (result.Data is TrackingCardData tc) cards = cards with { Tracking = tc };
                    else if (result.Data is ParcelListCardData plc) cards = cards with { ParcelList = plc };
                    else if (result.Data is ZonesCardData zc) cards = cards with { Zones = zc };
                    else if (result.Data is CourierAssignmentsCardData cac) cards = cards with { CourierAssignments = cac };
                    else if (result.Data is CourierWorkSummaryCardData cwc) cards = cards with { CourierWorkSummary = cwc };
                    else if (result.Data is CourierDutyCardData cdc) cards = cards with { CourierDuty = cdc };
                    else if (result.Data is CourierProtocolCardData cpc) cards = cards with { CourierProtocol = cpc };
                    else if (result.Data is DispatcherQueueCardData dqc) cards = cards with { DispatcherQueue = dqc };
                    else if (result.Data is CourierWorkloadCardData cwl) cards = cards with { CourierWorkload = cwl };
                    else if (result.Data is ZoneDispatchCardData zdc) cards = cards with { ZoneDispatch = zdc };
                    else if (result.Data is MerchantOrdersCardData moc) cards = cards with { MerchantOrders = moc };
                    else if (result.Data is MerchantIntegrationCardData mic) cards = cards with { MerchantIntegration = mic };
                    else if (result.Data is AdminReportCardData arc) cards = cards with { AdminReport = arc };
                    else if (result.Data is AdminTariffsCardData atc) cards = cards with { AdminTariffs = atc };
                    else if (result.Data is AdminOverviewCardData aoc) cards = cards with { AdminOverview = aoc };
                    else
                    {
                        try
                        {
                            var json = JsonSerializer.Serialize(result.Data, JsonOptions);
                            var d = JsonDocument.Parse(json);
                            if (d.RootElement.TryGetProperty("trackingNumber", out var tnProp))
                            {
                                var tn = tnProp.GetString() ?? "";
                                var st = d.RootElement.TryGetProperty("status", out var sp) ? sp.GetString() ?? "" : "";
                                var orig = d.RootElement.TryGetProperty("pickupCity", out var oc) ? oc.GetString() : d.RootElement.TryGetProperty("originCity", out var origC) ? origC.GetString() : "Colombo";
                                var dest = d.RootElement.TryGetProperty("deliveryCity", out var dc) ? dc.GetString() : d.RootElement.TryGetProperty("destinationCity", out var dstC) ? dstC.GetString() : "Kandy";
                                var hr = d.RootElement.TryGetProperty("hasReceipt", out var hrP) && hrP.GetBoolean();
                                var isPub = d.RootElement.TryGetProperty("isPublic", out var ipP) && ipP.GetBoolean();
                                var pId = d.RootElement.TryGetProperty("parcelId", out var piP) ? piP.GetString() : null;

                                var steps = new List<MilestoneStep>();
                                if (d.RootElement.TryGetProperty("milestones", out var ms) && ms.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var m in ms.EnumerateArray())
                                    {
                                        var sName = m.GetProperty("status").GetString() ?? "";
                                        var label = m.GetProperty("label").GetString() ?? "";
                                        var completed = m.GetProperty("completed").GetBoolean();
                                        var current = m.GetProperty("current").GetBoolean();
                                        DateTimeOffset? ts = m.TryGetProperty("timestamp", out var tProp) && tProp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(tProp.GetString(), out var parsedTs) ? parsedTs : null;
                                        steps.Add(new MilestoneStep(sName, label, ts, completed, current));
                                    }
                                }

                                cards = cards with
                                {
                                    Tracking = new TrackingCardData(tn, st, orig ?? "Origin", dest ?? "Destination", DateTimeOffset.UtcNow, steps, hr, pId, isPub)
                                };
                            }
                        }
                        catch { }
                    }
                }

                // Default reply text to result summary
                replyText = result.TextSummary;

                // Attempt multi-turn synthesis with Gemini
                try
                {
                    var functionResultData = result.Data ?? new { summary = result.TextSummary };
                    var followUpContents = new List<object>(contentsList)
                    {
                        new
                        {
                            role = "model",
                            parts = new object[]
                            {
                                new
                                {
                                    functionCall = new
                                    {
                                        name = toolName,
                                        args = argsObj
                                    }
                                }
                            }
                        },
                        new
                        {
                            role = "function",
                            parts = new object[]
                            {
                                new
                                {
                                    functionResponse = new
                                    {
                                        name = toolName,
                                        response = new { result = functionResultData }
                                    }
                                }
                            }
                        }
                    };

                    var secondRequestBody = new
                    {
                        contents = followUpContents,
                        system_instruction = systemInstruction
                    };

                    var secondResponse = await GeminiClient.PostAsJsonAsync(endpoint, secondRequestBody, JsonOptions, cancellationToken);
                    if (secondResponse.IsSuccessStatusCode)
                    {
                        using var secondDoc = await JsonDocument.ParseAsync(await secondResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                        if (secondDoc.RootElement.TryGetProperty("candidates", out var sc2) && sc2.GetArrayLength() > 0 &&
                            sc2[0].TryGetProperty("content", out var sContent) && sContent.TryGetProperty("parts", out var sParts))
                        {
                            var synthesized = string.Join("\n", sParts.EnumerateArray()
                                .Where(p => p.TryGetProperty("text", out _))
                                .Select(p => p.GetProperty("text").GetString()));

                            if (!string.IsNullOrWhiteSpace(synthesized))
                            {
                                replyText = synthesized.Trim();
                            }
                        }
                    }
                }
                catch
                {
                    // Fail-safe: Keep result.TextSummary
                }
            }
        }

        if (string.IsNullOrWhiteSpace(replyText))
        {
            replyText = "Here is the information from your SwiftDrop delivery service.";
        }

        return new AssistantChatResponse(
            Reply: replyText.Trim(),
            Mode: $"gemini ({model})",
            ToolCalls: toolCalls,
            Cards: cards,
            SuggestedPrompts: GetSuggestedPromptsForRole(role));
    }
}
