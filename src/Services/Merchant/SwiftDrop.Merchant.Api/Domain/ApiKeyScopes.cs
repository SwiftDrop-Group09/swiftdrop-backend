namespace SwiftDrop.Merchant.Api.Domain;

public static class ApiKeyScopes
{
    public const string OrdersWrite = "orders:write";
    public const string OrdersRead = "orders:read";
    public const string WebhooksManage = "webhooks:manage";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        OrdersWrite,
        OrdersRead,
        WebhooksManage
    };

    public static IReadOnlyList<string> ParseAndValidate(IEnumerable<string> requestedScopes)
    {
        ArgumentNullException.ThrowIfNull(requestedScopes);

        var scopesList = requestedScopes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (scopesList.Count == 0)
        {
            throw new ArgumentException("At least one scope is required.", nameof(requestedScopes));
        }

        foreach (var scope in scopesList)
        {
            if (!All.Contains(scope))
            {
                throw new ArgumentException($"Invalid scope '{scope}'. Allowed scopes: {string.Join(", ", All)}", nameof(requestedScopes));
            }
        }

        return scopesList;
    }
}
