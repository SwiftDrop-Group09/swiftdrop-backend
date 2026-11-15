using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Assistant;

public sealed class AssistantToolExecutor(
    ICurrentCustomer currentCustomer,
    ICurrentDeliveryUser currentDeliveryUser,
    CustomerTrackingHandler trackingHandler,
    QuoteEstimator quoteEstimator,
    IBookingZoneCatalog zoneCatalog,
    IQuoteStore quoteStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ToFriendlyStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "created" or "booked" => "Booked",
        "assigned" => "Assigned",
        "pickedup" => "Collected",
        "intransit" => "In transit",
        "outfordelivery" => "Out for delivery",
        "delivered" => "Delivered",
        "failed" => "Delivery unsuccessful",
        "cancelled" => "Cancelled",
        _ => status ?? "Booked"
    };

    public async Task<ToolExecutionResult> ExecuteAsync(
        string toolName,
        JsonElement? arguments,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        return toolName switch
        {
            AssistantToolNames.TrackParcel => await ExecuteTrackParcelAsync(arguments, cancellationToken),
            AssistantToolNames.ListMyParcels => await ExecuteListMyParcelsAsync(arguments, cancellationToken),
            AssistantToolNames.EstimateDeliveryFee => await ExecuteEstimateFeeAsync(arguments, cancellationToken),
            AssistantToolNames.ListServiceZones => await ExecuteListServiceZonesAsync(arguments, cancellationToken),
            _ => new ToolExecutionResult(toolName, false, null, $"Unknown tool '{toolName}'. Allowed tools: {string.Join(", ", AssistantToolNames.All)}.", "unknown_tool")
        };
    }

    // -------------------------------------------------------------
    // Tool: track_parcel
    // -------------------------------------------------------------
    private async Task<ToolExecutionResult> ExecuteTrackParcelAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var input = Deserialize<TrackParcelInput>(arguments) ?? new TrackParcelInput();
        var trackingNumber = input.TrackingNumber?.Trim();

        // If no tracking number supplied, check if caller is an authenticated Customer with parcels
        if (string.IsNullOrWhiteSpace(trackingNumber))
        {
            if (!currentDeliveryUser.IsAuthenticated || !currentDeliveryUser.IsCustomer)
            {
                return new ToolExecutionResult(
                    AssistantToolNames.TrackParcel,
                    false,
                    null,
                    "Please provide a tracking number (e.g. SD-2026-0001) or sign in to your customer account to track your parcels.",
                    "missing_tracking_number");
            }

            var recentResult = await trackingHandler.ListParcelsAsync(
                new CustomerParcelQueryInput(PageSize: "5"), cancellationToken);

            if (recentResult.Response is not { } page || page.Items.Count == 0)
            {
                return new ToolExecutionResult(
                    AssistantToolNames.TrackParcel,
                    true,
                    new { parcels = Array.Empty<object>() },
                    "You do not have any active or past parcels in your account yet.",
                    null);
            }

            var latest = page.Items[0];
            trackingNumber = latest.TrackingNumber;
        }

        // 1. Try customer-scoped lookup if authenticated as customer
        if (currentDeliveryUser.IsAuthenticated && currentDeliveryUser.IsCustomer)
        {
            var customerListResult = await trackingHandler.ListParcelsAsync(
                new CustomerParcelQueryInput(Search: trackingNumber, PageSize: "1"), cancellationToken);

            if (customerListResult.Response is { } customerPage && customerPage.Items.Count > 0)
            {
                var parcel = customerPage.Items[0];
                var eventsResult = await trackingHandler.GetParcelEventsAsync(parcel.Id.ToString(), false, cancellationToken);
                var publicResult = await trackingHandler.GetPublicTrackingAsync(trackingNumber, false, cancellationToken);

                var steps = BuildMilestones(parcel.Status, publicResult.Response?.Timeline);
                var trackingCard = new TrackingCardData(
                    parcel.TrackingNumber,
                    parcel.Status,
                    parcel.PickupCity,
                    parcel.DeliveryCity,
                    parcel.UpdatedAt,
                    steps,
                    parcel.Status.Equals("Delivered", StringComparison.OrdinalIgnoreCase),
                    parcel.Id.ToString(),
                    IsPublic: false);

                return new ToolExecutionResult(
                    AssistantToolNames.TrackParcel,
                    true,
                    new
                    {
                        trackingNumber = parcel.TrackingNumber,
                        status = parcel.Status,
                        pickupCity = parcel.PickupCity,
                        deliveryCity = parcel.DeliveryCity,
                        updatedAt = parcel.UpdatedAt,
                        isOwner = true,
                        isPublic = false,
                        hasReceipt = parcel.Status.Equals("Delivered", StringComparison.OrdinalIgnoreCase),
                        milestones = steps,
                        parcelId = parcel.Id.ToString()
                    },
                    $"Your parcel **{parcel.TrackingNumber}** from **{parcel.PickupCity}** to **{parcel.DeliveryCity}** is currently **{ToFriendlyStatus(parcel.Status)}**.",
                    null);
            }
        }

        // 2. Fall back to safe public tracking (zero PII, redacted addresses)
        var publicLookup = await trackingHandler.GetPublicTrackingAsync(trackingNumber, false, cancellationToken);
        if (publicLookup.Response is { } publicSummary)
        {
            var steps = BuildMilestones(publicSummary.Status, publicSummary.Timeline);
            var card = new TrackingCardData(
                publicSummary.TrackingNumber,
                publicSummary.Status,
                publicSummary.OriginCity,
                publicSummary.DestinationCity,
                publicSummary.LastUpdatedAt,
                steps,
                false,
                null,
                IsPublic: true);

            return new ToolExecutionResult(
                AssistantToolNames.TrackParcel,
                true,
                new
                {
                    trackingNumber = publicSummary.TrackingNumber,
                    status = publicSummary.Status,
                    originCity = publicSummary.OriginCity,
                    destinationCity = publicSummary.DestinationCity,
                    lastUpdatedAt = publicSummary.LastUpdatedAt,
                    isOwner = false,
                    isPublic = true,
                    milestones = steps
                },
                $"Parcel **{publicSummary.TrackingNumber}** from **{publicSummary.OriginCity}** to **{publicSummary.DestinationCity}** is currently **{ToFriendlyStatus(publicSummary.Status)}**.",
                null);
        }

        return new ToolExecutionResult(
            AssistantToolNames.TrackParcel,
            false,
            null,
            $"No parcel was found matching tracking number '{trackingNumber}'. Please verify the tracking code and try again.",
            "parcel_not_found");
    }

    // -------------------------------------------------------------
    // Tool: list_my_parcels
    // -------------------------------------------------------------
    private async Task<ToolExecutionResult> ExecuteListMyParcelsAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        if (!currentDeliveryUser.IsAuthenticated || !currentDeliveryUser.IsCustomer)
        {
            return new ToolExecutionResult(
                AssistantToolNames.ListMyParcels,
                false,
                null,
                "Authentication required: You must be signed in as a customer to view your parcel list. Please sign in or track by tracking number.",
                "auth_required");
        }

        var input = Deserialize<ListMyParcelsInput>(arguments) ?? new ListMyParcelsInput();
        var limit = Math.Clamp(input.Limit ?? 5, 1, 10);

        var queryInput = new CustomerParcelQueryInput(
            Status: input.Status,
            Search: input.Search,
            PageSize: limit.ToString());

        var result = await trackingHandler.ListParcelsAsync(queryInput, cancellationToken);
        if (result.Response is not { } page)
        {
            return new ToolExecutionResult(
                AssistantToolNames.ListMyParcels,
                false,
                null,
                "Unable to retrieve your deliveries at this moment. Please try again shortly.",
                result.Failure?.ToString() ?? "unknown_error");
        }

        var summaryItems = page.Items.Select(item => new ParcelSummaryItem(
            item.TrackingNumber,
            item.Status,
            $"{item.PickupCity} → {item.DeliveryCity}",
            item.PackageDetails.WeightKg,
            item.PackageDetails.Size,
            item.CreatedAt,
            item.Id.ToString())).ToList();

        var cardData = new ParcelListCardData(page.TotalCount, summaryItems);

        var textSummary = summaryItems.Count == 0
            ? "You have no parcels matching the requested filter."
            : $"Found {page.TotalCount} parcel(s). Showing the latest {summaryItems.Count}:\n" +
              string.Join("\n", summaryItems.Select(p => $"- **{p.TrackingNumber}** ({p.Status}) - {p.Route} [{p.WeightKg} kg, {p.Size}]"));

        return new ToolExecutionResult(
            AssistantToolNames.ListMyParcels,
            true,
            cardData,
            textSummary,
            null);
    }

    // -------------------------------------------------------------
    // Tool: estimate_delivery_fee
    // -------------------------------------------------------------
    private async Task<ToolExecutionResult> ExecuteEstimateFeeAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var input = Deserialize<EstimateFeeInput>(arguments) ?? new EstimateFeeInput();

        if (input.WeightKg is null or <= 0 or > 30)
        {
            var msg = input.WeightKg > 30
                ? $"SwiftDrop currently supports parcels up to 30 kg. We cannot process or estimate delivery fees for a {input.WeightKg:G29} kg parcel."
                : "Invalid parcel weight. Weight must be greater than zero and at most 30 kg.";

            return new ToolExecutionResult(
                AssistantToolNames.EstimateDeliveryFee,
                false,
                null,
                msg,
                "invalid_weight");
        }

        var weight = input.WeightKg.Value;
        var activeZones = await zoneCatalog.ReadActiveAsync(cancellationToken);

        if (activeZones.Count == 0)
        {
            return new ToolExecutionResult(
                AssistantToolNames.EstimateDeliveryFee,
                false,
                null,
                "No delivery service zones are currently active.",
                "no_active_zones");
        }

        // Infer Size if omitted
        var resolvedSize = InferSize(weight, input.Size);

        if (string.IsNullOrWhiteSpace(input.PickupCity) && string.IsNullOrWhiteSpace(input.PickupZoneId))
        {
            var dest = !string.IsNullOrWhiteSpace(input.DeliveryCity) ? $" to {input.DeliveryCity.Trim()}" : "";
            return new ToolExecutionResult(
                AssistantToolNames.EstimateDeliveryFee,
                false,
                null,
                $"Where will the parcel be picked up from? Please specify the starting position (pickup city) to calculate an accurate delivery fee{dest}.",
                "missing_pickup_city");
        }

        // Resolve pickup and delivery zones
        var pickupZone = ResolveZone(input.PickupZoneId, input.PickupCity, activeZones, isPickup: true);
        var deliveryZone = ResolveZone(input.DeliveryZoneId, input.DeliveryCity, activeZones, isPickup: false);

        if (pickupZone is null || deliveryZone is null)
        {
            var supported = string.Join(", ", activeZones.Select(z => z.Name));
            return new ToolExecutionResult(
                AssistantToolNames.EstimateDeliveryFee,
                false,
                null,
                $"Unable to resolve service zone for '{(pickupZone is null ? input.PickupCity ?? "Pickup" : input.DeliveryCity ?? "Delivery")}'. Active zones: {supported}.",
                "unsupported_zone");
        }

        var pickupCity = !string.IsNullOrWhiteSpace(input.PickupCity) ? input.PickupCity.Trim() : pickupZone.Name;
        var deliveryCity = !string.IsNullOrWhiteSpace(input.DeliveryCity) ? input.DeliveryCity.Trim() : deliveryZone.Name;

        // If authenticated Customer, calculate and persist an owner-bound quote via QuoteEstimator
        if (currentCustomer.IsAuthenticated && currentCustomer.IsCustomer)
        {
            var quoteInput = new QuoteInput(
                PickupAddress: new AddressInput("Main St", pickupCity, "10100", pickupZone.Id.ToString()),
                DeliveryAddress: new AddressInput("Cross St", deliveryCity, "20000", deliveryZone.Id.ToString()),
                PackageDetails: new PackageDetailsInput(weight, resolvedSize),
                Recipient: new RecipientInput("Recipient", "+94771234567"));

            var estimateResult = await quoteEstimator.ExecuteAsync(quoteInput, cancellationToken);
            if (estimateResult.Quote is { } quote)
            {
                var deepLink = $"/customer/book?quoteId={quote.QuoteId}&pickupCity={Uri.EscapeDataString(pickupCity)}&deliveryCity={Uri.EscapeDataString(deliveryCity)}&weight={weight}&size={resolvedSize}";

                var card = new QuoteCardData(
                    quote.QuoteId,
                    quote.Total,
                    quote.Currency,
                    quote.PricingVersion,
                    quote.ExpiresAt,
                    weight,
                    resolvedSize,
                    pickupCity,
                    deliveryCity,
                    deepLink);

                var expiryText = quote.ExpiresAt.ToString("h:mm tt");
                var summaryText = $"Sending a **{weight} kg ({resolvedSize})** parcel from **{pickupCity}** to **{deliveryCity}** costs **{quote.Currency} {quote.Total:N2}**. This quote is locked until {expiryText}—you can proceed to book directly below.";

                return new ToolExecutionResult(
                    AssistantToolNames.EstimateDeliveryFee,
                    true,
                    card,
                    summaryText,
                    null);
            }

            return new ToolExecutionResult(
                AssistantToolNames.EstimateDeliveryFee,
                false,
                null,
                $"Pricing calculation failed: {estimateResult.Failure?.ToString() ?? "pricing_unavailable"}",
                estimateResult.Failure?.ToString() ?? "pricing_unavailable");
        }

        // Guest / Public rate preview
        if (!Enum.TryParse<ParcelSize>(resolvedSize, true, out var parsedSizeEnum))
        {
            parsedSizeEnum = ParcelSize.Small;
        }

        var pricing = await quoteStore.ReadPricingAsync(pickupZone.Id, deliveryZone.Id, parsedSizeEnum, cancellationToken);
        if (pricing.ActiveTiers.Count == 1)
        {
            var tier = pricing.ActiveTiers[0];
            if (PriceCalculator.TryCalculate(tier.BaseRate, tier.PerKgRate, weight, out var calculated))
            {
                var deepLink = $"/customer/book?pickupCity={Uri.EscapeDataString(pickupCity)}&deliveryCity={Uri.EscapeDataString(deliveryCity)}&weight={weight}&size={resolvedSize}";

                var card = new QuoteCardData(
                    null,
                    calculated.Total,
                    tier.Currency,
                    tier.PricingVersion,
                    null,
                    weight,
                    resolvedSize,
                    pickupCity,
                    deliveryCity,
                    deepLink);

                var summaryText = $"Estimated delivery fee for a **{weight} kg ({resolvedSize})** parcel from **{pickupCity}** to **{deliveryCity}** is approximately **{tier.Currency} {calculated.Total:N2}**. Sign in to lock an official quote.";

                return new ToolExecutionResult(
                    AssistantToolNames.EstimateDeliveryFee,
                    true,
                    card,
                    summaryText,
                    null);
            }
        }

        return new ToolExecutionResult(
            AssistantToolNames.EstimateDeliveryFee,
            false,
            null,
            $"Active pricing tier not found for route {pickupZone.Name} → {deliveryZone.Name} ({resolvedSize}).",
            "pricing_unavailable");
    }

    // -------------------------------------------------------------
    // Tool: list_service_zones
    // -------------------------------------------------------------
    private async Task<ToolExecutionResult> ExecuteListServiceZonesAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var activeZones = await zoneCatalog.ReadActiveAsync(cancellationToken);

        var zonesList = activeZones.Select(zone => new ZoneCoverageItem(
            zone.Id,
            zone.Name,
            [zone.Name],
            true)).ToList();

        var cities = activeZones.Select(z => z.Name).ToList();
        var card = new ZonesCardData(zonesList, cities);

        return new ToolExecutionResult(
            AssistantToolNames.ListServiceZones,
            true,
            card,
            $"SwiftDrop currently covers {activeZones.Count} active delivery zone(s): {string.Join(", ", cities)}.",
            null);
    }

    // -------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------

    private static string InferSize(decimal weightKg, string? requestedSize)
    {
        if (!string.IsNullOrWhiteSpace(requestedSize))
        {
            var trimmed = requestedSize.Trim();
            if (Regex.IsMatch(trimmed, @"\b(sma?ll?|sml)\b", RegexOptions.IgnoreCase)) return "Small";
            if (Regex.IsMatch(trimmed, @"\b(medi?u?m|med)\b", RegexOptions.IgnoreCase)) return "Medium";
            if (Regex.IsMatch(trimmed, @"\b(la?rge?|lrg)\b", RegexOptions.IgnoreCase)) return "Large";
        }

        return weightKg switch
        {
            <= 2m => "Small",
            <= 5m => "Medium",
            _ => "Large"
        };
    }

    private static BookableZone? ResolveZone(
        string? zoneIdString,
        string? cityName,
        IReadOnlyList<BookableZone> zones,
        bool isPickup)
    {
        if (Guid.TryParse(zoneIdString, out var zoneId))
        {
            var match = zones.FirstOrDefault(z => z.Id == zoneId);
            if (match is not null) return match;
        }

        if (string.IsNullOrWhiteSpace(cityName))
        {
            return null;
        }

        var rawCity = cityName.Trim();

        // 1. Exact match (case-insensitive)
        var exact = zones.FirstOrDefault(z => z.Name.Equals(rawCity, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // 2. Substring match
        var partial = zones.FirstOrDefault(z =>
            z.Name.Contains(rawCity, StringComparison.OrdinalIgnoreCase) ||
            rawCity.Contains(z.Name, StringComparison.OrdinalIgnoreCase));
        if (partial is not null) return partial;

        // 3. Normalized alphanumeric match
        var normInput = NormalizeCityName(rawCity);
        var normMatch = zones.FirstOrDefault(z => NormalizeCityName(z.Name).Equals(normInput, StringComparison.OrdinalIgnoreCase));
        if (normMatch is not null) return normMatch;

        // 4. Common Sri Lankan city aliases & province fallbacks
        var aliasTarget = ResolveCommonAlias(normInput);
        if (aliasTarget != null)
        {
            var aliasZone = zones.FirstOrDefault(z => z.Name.Equals(aliasTarget, StringComparison.OrdinalIgnoreCase)
                || z.Name.Contains(aliasTarget, StringComparison.OrdinalIgnoreCase));
            if (aliasZone is not null) return aliasZone;
        }

        // 5. Fuzzy matching with Levenshtein distance against active zones
        BookableZone? closestZone = null;
        var minDistance = int.MaxValue;

        foreach (var z in zones)
        {
            var normZone = NormalizeCityName(z.Name);
            var dist = LevenshteinDistance(normInput, normZone);
            var maxAllowedDist = normZone.Length switch
            {
                <= 4 => 1,
                <= 7 => 2, // e.g. "jafna" (5) vs "jaffna" (6) -> dist 1 <= 2
                _ => 3    // e.g. "rathnapura" (11) vs "ratnapura" (10) -> dist 1 <= 3
            };

            if (dist <= maxAllowedDist && dist < minDistance)
            {
                minDistance = dist;
                closestZone = z;
            }
        }

        return closestZone;
    }

    private static string NormalizeCityName(string text)
    {
        return Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]", "");
    }

    private static string? ResolveCommonAlias(string norm)
    {
        return norm switch
        {
            "jafna" or "yaalpnam" or "jaffna" => "Jaffna",
            "colambo" or "kolombo" or "colombo" or "moratuwa" or "dehiwala" or "kotte" or "nugegoda" or "maharagama" => "Colombo",
            "kandi" or "nuwara" or "kandy" => "Kandy",
            "gale" or "galle" => "Galle",
            "trinco" or "trincomale" or "trincomalee" => "Trincomalee",
            "batti" or "batticoloa" or "batticaloa" => "Batticaloa",
            "kurunagala" or "kurunagale" or "kurunegale" or "kurunegala" => "Kurunegala",
            "rathnapura" or "ratnapure" or "ratnapura" => "Ratnapura",
            "anuradapura" or "anuradhapure" or "anuradhapura" => "Anuradhapura",
            "polonnaruwe" or "polonnaruwa" => "Polonnaruwa",
            "matare" or "matara" => "Matara",
            "nuwaraeliya" or "eliya" => "Nuwara Eliya",
            "negombo" or "katunayake" or "kelaniya" or "jaela" or "gampaha" => "Gampaha",
            _ => null
        };
    }

    private static int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
        if (string.IsNullOrEmpty(t)) return s.Length;

        var d = new int[s.Length + 1, t.Length + 1];

        for (var i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= t.Length; j++) d[0, j] = j;

        for (var i = 1; i <= s.Length; i++)
        {
            for (var j = 1; j <= t.Length; j++)
            {
                var cost = (s[i - 1] == t[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[s.Length, t.Length];
    }

    private static IReadOnlyList<MilestoneStep> BuildMilestones(
        string currentStatus,
        IReadOnlyList<PublicTimelineEntry>? timeline)
    {
        var standardMilestones = new[]
        {
            ("Booked", "Order Placed"),
            ("Assigned", "Courier Assigned"),
            ("PickedUp", "Picked Up"),
            ("InTransit", "In Transit"),
            ("Delivered", "Delivered")
        };

        var statusOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Booked"] = 1,
            ["Assigned"] = 2,
            ["PickedUp"] = 3,
            ["InTransit"] = 4,
            ["Delivered"] = 5,
            ["Failed"] = 5,
            ["Cancelled"] = 5
        };

        var currentRank = statusOrder.TryGetValue(currentStatus, out var r) ? r : 1;

        return standardMilestones.Select(m =>
        {
            var rank = statusOrder[m.Item1];
            var timestamp = timeline?.FirstOrDefault(t => t.Status.Equals(m.Item1, StringComparison.OrdinalIgnoreCase))?.Timestamp;
            return new MilestoneStep(
                m.Item1,
                m.Item2,
                timestamp,
                Completed: rank <= currentRank,
                Current: m.Item1.Equals(currentStatus, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    private static T? Deserialize<T>(JsonElement? element) where T : class
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(element.Value.GetRawText(), JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
