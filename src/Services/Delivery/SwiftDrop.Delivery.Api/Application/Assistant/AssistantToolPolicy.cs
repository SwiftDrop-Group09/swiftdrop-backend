using System.Text.Json;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;

namespace SwiftDrop.Delivery.Api.Application.Assistant;

internal static class AssistantToolPolicy
{
    private static readonly IReadOnlyDictionary<string, JsonElement> Schemas = McpServerHandler.GetCatalog()
        .ToDictionary(item => item.Name, item => JsonSerializer.SerializeToElement(item.InputSchema), StringComparer.Ordinal);

    public static bool CanUse(ICurrentDeliveryUser user, string tool)
    {
        if (tool is AssistantToolNames.TrackParcel or AssistantToolNames.EstimateDeliveryFee
            or AssistantToolNames.EstimateDeliveryTime or AssistantToolNames.ListServiceZones)
            return true;

        if (!user.IsAuthenticated || user.UserId is null)
            return false;

        return tool switch
        {
            AssistantToolNames.ListMyParcels => user.IsCustomer,
            AssistantToolNames.GetCourierAssignedDeliveries or AssistantToolNames.GetCourierWorkSummary
                or AssistantToolNames.GetCourierDutyStatus or AssistantToolNames.GetCourierDeliveryProtocol => user.IsCourier,
            AssistantToolNames.ListUnassignedParcels or AssistantToolNames.GetCourierWorkload
                or AssistantToolNames.GetZoneDispatchSummary => user.IsDispatcher || user.IsAdmin,
            AssistantToolNames.ListMerchantOrders or AssistantToolNames.GetMerchantIntegrationGuide => user.IsMerchant || user.IsAdmin,
            AssistantToolNames.GetDeliverySystemReports or AssistantToolNames.ListPricingTariffs
                or AssistantToolNames.GetAdminSystemOverview => user.IsAdmin,
            _ => false
        };
    }

    public static bool ValidArguments(string tool, JsonElement? arguments, int maxCharacters)
    {
        var schema = Schemas[tool];
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        if (arguments is { } args)
        {
            if (args.ValueKind != JsonValueKind.Object || args.GetRawText().Length > maxCharacters)
                return false;

            var properties = schema.GetProperty("properties");
            foreach (var argument in args.EnumerateObject())
            {
                if (!properties.TryGetProperty(argument.Name, out var definition) ||
                    !supplied.Add(argument.Name) || !ValidValue(argument.Value, definition))
                    return false;

            }
        }

        return !schema.TryGetProperty("required", out var required) ||
            arguments is { } provided && required.EnumerateArray().All(property =>
                provided.TryGetProperty(property.GetString()!, out var value) && value.ValueKind != JsonValueKind.Null);
    }

    private static bool ValidValue(JsonElement value, JsonElement definition)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return true;

        return definition.GetProperty("type").GetString() switch
        {
            "string" => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 128 } text &&
                !text.Any(char.IsControl) && (!definition.TryGetProperty("enum", out var values) ||
                    values.EnumerateArray().Any(item => string.Equals(item.GetString(), text, StringComparison.OrdinalIgnoreCase))),
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            _ => false
        };
    }
}
