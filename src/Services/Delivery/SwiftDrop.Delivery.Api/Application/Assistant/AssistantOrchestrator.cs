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

        // 1. Guardrail / Injection Check
        if (IsGuardrailViolation(rawMessage, out var guardrailReason))
        {
            return new AssistantChatResponse(
                Reply: "⚠️ **Security Guardrail Triggered**\n\n" +
                       "Autonomous administrative actions, write operations (booking, courier assignment, status changes, refunds), " +
                       "and cross-tenant data access are strictly prohibited.\n\n" +
                       "The SwiftDrop AI Delivery Assistant operates exclusively in read-only tracking and quote estimation mode. " +
                       "Please use the standard authenticated management portals for role-authorized administrative tasks.",
                Mode: "mcp-local-agent",
                ToolCalls: Array.Empty<AssistantToolCallTrace>(),
                Cards: null,
                SuggestedPrompts:
                [
                    "Where is my parcel?",
                    "How much to send 2 kg to Kandy?",
                    "What delivery zones do you cover?"
                ],
                Guardrail: new GuardrailResult(Blocked: true, Reason: guardrailReason));
        }

        // 2. Check if external LLM mode is configured and reachable
        var apiKey = configuration["AI:ApiKey"]
            ?? configuration["DELIVERY_AI_API_KEY"]
            ?? configuration["GEMINI_API_KEY"]
            ?? configuration["OPENAI_API_KEY"];

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var geminiResponse = await ProcessWithGeminiAsync(rawMessage, apiKey.Trim(), cancellationToken);
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

        // 3. Built-In Resilient Deterministic Tool-Calling Engine
        return await ProcessWithBuiltInEngineAsync(rawMessage, cancellationToken);
    }

    private async Task<AssistantChatResponse> ProcessWithBuiltInEngineAsync(
        string message,
        CancellationToken cancellationToken)
    {
        var toolCalls = new List<AssistantToolCallTrace>();
        var cards = new AssistantCards();

        // 1. Fee Estimation Intent (e.g. "How much to send 2 kg to Kandy?", "Cost of 5kg parcel from Colombo to Galle")
        if (IsFeeEstimateIntent(message, out var feeArgs))
        {
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

            var reply = result.Success
                ? $"### Delivery Fee Estimate\n\n{result.TextSummary}\n\n" +
                  (quoteCard?.QuoteId is not null
                      ? "Your quote has been calculated and locked against our versioned tariff. You can confirm your booking directly below:"
                      : "This is an uncommitted fee preview. Sign in to your Customer account to lock this rate and book your delivery.")
                : $"### Delivery Fee Estimate\n\nUnable to calculate fee: {result.TextSummary}";

            return new AssistantChatResponse(
                Reply: reply,
                Mode: "mcp-local-agent",
                ToolCalls: toolCalls,
                Cards: new AssistantCards(Quote: quoteCard),
                SuggestedPrompts:
                [
                    "Where is my parcel?",
                    "What delivery zones do you cover?",
                    "List my recent deliveries"
                ]);
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

            if (result.Success && result.Data is not null)
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
                        hasReceipt);
                }
            }

            var reply = result.Success
                ? $"### Parcel Tracking\n\n{result.TextSummary}"
                : $"### Tracking Lookup\n\n{result.TextSummary}";

            return new AssistantChatResponse(
                Reply: reply,
                Mode: "mcp-local-agent",
                ToolCalls: toolCalls,
                Cards: new AssistantCards(Tracking: trackingCard, ParcelList: listCard),
                SuggestedPrompts:
                [
                    "How much to send 2 kg to Kandy?",
                    "What delivery zones do you cover?",
                    "List my recent deliveries"
                ]);
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
                Reply: $"### Your Deliveries\n\n{result.TextSummary}",
                Mode: "mcp-local-agent",
                ToolCalls: toolCalls,
                Cards: new AssistantCards(ParcelList: listCard),
                SuggestedPrompts:
                [
                    "Where is my parcel?",
                    "How much to send 2 kg to Kandy?",
                    "What delivery zones do you cover?"
                ]);
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
                Reply: $"### Service Coverage\n\n{result.TextSummary}",
                Mode: "mcp-local-agent",
                ToolCalls: toolCalls,
                Cards: new AssistantCards(Zones: zonesCard),
                SuggestedPrompts:
                [
                    "How much to send 2 kg to Kandy?",
                    "Where is my parcel?",
                    "List my recent deliveries"
                ]);
        }

        // 5. Default Greeting & Capabilities
        return new AssistantChatResponse(
            Reply: "Hello! I am your **SwiftDrop AI Delivery Assistant**, powered by backend **Model Context Protocol (MCP)** tools.\n\n" +
                   "Here are a few things I can assist you with:\n\n" +
                   "- 📦 **Track a Delivery**: Ask *\"Where is my parcel?\"* or *\"Track SD-2026-0001\"* for real-time status and milestone progress.\n" +
                   "- 💰 **Estimate Fees**: Ask *\"How much to send 2 kg to Kandy?\"* for instant, versioned price quotes.\n" +
                   "- 📋 **View Deliveries**: Ask *\"List my active deliveries\"* to see your shipments.\n" +
                   "- 🗺️ **Coverage & Zones**: Ask *\"What delivery zones do you cover?\"* to check our active delivery regions.\n\n" +
                   "How can I help you today?",
            Mode: "mcp-local-agent",
            ToolCalls: Array.Empty<AssistantToolCallTrace>(),
            Cards: null,
            SuggestedPrompts:
            [
                "Where is my parcel?",
                "How much to send 2 kg to Kandy?",
                "What delivery zones do you cover?",
                "List my recent deliveries"
            ]);
    }

    // -------------------------------------------------------------
    // Intent Recognizers & Security Guardrails
    // -------------------------------------------------------------

    private static bool IsGuardrailViolation(string text, out string reason)
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

        // 3. Cross-tenant inspection
        if (Regex.IsMatch(lower, @"\b(all customers|other users|show all parcels|other customer's|dump database|dump all)\b"))
        {
            reason = "Cross-tenant data exposure request blocked by server security policy.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool IsFeeEstimateIntent(string text, out EstimateFeeInput args)
    {
        var lower = text.ToLowerInvariant();
        var hasPricingKeyword = Regex.IsMatch(lower, @"\b(how much|cost|price|fee|rate|quote|estimate|send.*to|send.*kg)\b");

        if (!hasPricingKeyword)
        {
            args = new EstimateFeeInput();
            return false;
        }

        decimal weight = 2.0m;
        var weightMatch = Regex.Match(lower, @"(\d+(\.\d+)?)\s*(kg|kilo|kilogram)?");
        if (weightMatch.Success && decimal.TryParse(weightMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var parsedWeight) && parsedWeight > 0)
        {
            weight = parsedWeight;
        }

        // Extract destination city (ignoring verbs like 'send', 'ship', 'deliver')
        string? deliveryCity = null;
        var toMatches = Regex.Matches(text, @"\b(?:to|into)\s+(?!send\b|ship\b|deliver\b|post\b)([A-Za-z]+)", RegexOptions.IgnoreCase);
        if (toMatches.Count > 0)
        {
            deliveryCity = toMatches[^1].Groups[1].Value;
        }
        else
        {
            // Fallback match known cities
            var knownCities = new[] { "Kandy", "Colombo", "Galle", "Negombo", "Jaffna", "Kurunegala", "Matara", "Gampaha" };
            foreach (var city in knownCities)
            {
                if (lower.Contains(city.ToLowerInvariant()))
                {
                    deliveryCity = city;
                    break;
                }
            }
        }

        // Extract origin city if specified
        string? pickupCity = null;
        var fromMatch = Regex.Match(text, @"from\s+([A-Za-z]+)", RegexOptions.IgnoreCase);
        if (fromMatch.Success)
        {
            pickupCity = fromMatch.Groups[1].Value;
        }

        // Extract size if specified
        string? size = null;
        if (lower.Contains("small")) size = "Small";
        else if (lower.Contains("medium")) size = "Medium";
        else if (lower.Contains("large")) size = "Large";

        args = new EstimateFeeInput(
            WeightKg: weight,
            Size: size,
            PickupCity: pickupCity,
            DeliveryCity: deliveryCity ?? "Kandy");

        return true;
    }

    private static bool IsTrackingIntent(string text, out TrackParcelInput args)
    {
        var trackingMatch = Regex.Match(text, @"(SD-\d{4}-\d+|[A-Z0-9]{4,10}-\d{4,})", RegexOptions.IgnoreCase);
        if (trackingMatch.Success)
        {
            args = new TrackParcelInput(TrackingNumber: trackingMatch.Groups[1].Value.ToUpperInvariant());
            return true;
        }

        var lower = text.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"\b(where is my parcel|track|status of|where is delivery|track parcel)\b"))
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
        string message,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var model = configuration["GEMINI_MODEL"] ?? "gemini-2.5-flash";
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        var requestBody = new
        {
            contents = new object[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = message }
                    }
                }
            },
            system_instruction = new
            {
                parts = new object[]
                {
                    new { text = "You are the SwiftDrop AI Delivery Assistant, helping users in Sri Lanka with parcel tracking, delivery rate quotes, and service zones. Always call the provided tools when real parcel status or fee calculations are needed. Never invent tracking milestones or prices without calling tools. Never execute write operations or impersonate admins. Format currency as LKR." }
                }
            },
            tools = new object[]
            {
                new
                {
                    function_declarations = new object[]
                    {
                        new
                        {
                            name = AssistantToolNames.EstimateDeliveryFee,
                            description = "Estimates delivery fee based on package weight in kg, pickup city, and delivery city in Sri Lanka.",
                            parameters = new
                            {
                                type = "OBJECT",
                                properties = new Dictionary<string, object>
                                {
                                    ["weightKg"] = new { type = "NUMBER", description = "Weight in kilograms (e.g. 2.0)" },
                                    ["pickupCity"] = new { type = "STRING", description = "Origin city in Sri Lanka (default Colombo)" },
                                    ["deliveryCity"] = new { type = "STRING", description = "Destination city in Sri Lanka (e.g. Kandy, Galle, Jaffna)" },
                                    ["size"] = new { type = "STRING", @enum = new[] { "Small", "Medium", "Large", "ExtraLarge" }, description = "Package size tier" }
                                },
                                required = new[] { "weightKg" }
                            }
                        },
                        new
                        {
                            name = AssistantToolNames.TrackParcel,
                            description = "Tracks parcel delivery status, location, and milestone timeline by tracking number (e.g. SD-...).",
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
                        }
                    }
                }
            }
        };

        var response = await GeminiClient.PostAsJsonAsync(endpoint, requestBody, JsonOptions, cancellationToken);
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
                var args = fc.TryGetProperty("args", out var a) ? a : JsonSerializer.SerializeToElement(new { });

                var sw = Stopwatch.StartNew();
                var result = await toolExecutor.ExecuteAsync(toolName, args, cancellationToken);
                sw.Stop();

                toolCalls.Add(new AssistantToolCallTrace(
                    toolName,
                    args,
                    result.Success ? "success" : "error",
                    sw.ElapsedMilliseconds,
                    result.TextSummary));

                if (result.Success && result.Data is not null)
                {
                    if (result.Data is QuoteCardData qc) cards = cards with { Quote = qc };
                    else if (result.Data is TrackingCardData tc) cards = cards with { Tracking = tc };
                    else if (result.Data is ParcelListCardData plc) cards = cards with { ParcelList = plc };
                    else if (result.Data is ZonesCardData zc) cards = cards with { Zones = zc };
                }

                if (string.IsNullOrWhiteSpace(replyText))
                {
                    replyText = result.TextSummary;
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
            Cards: (cards.Quote != null || cards.Tracking != null || cards.ParcelList != null || cards.Zones != null) ? cards : null,
            SuggestedPrompts:
            [
                "Where is my parcel?",
                "How much to send 2 kg to Kandy?",
                "What delivery zones do you cover?"
            ]);
    }
}
