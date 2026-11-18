using System.Globalization;

namespace SwiftDrop.Merchant.Api.Application.Orders.Dashboard;

/// <summary>
/// DateQuery rules: <c>from</c> inclusive, <c>to</c> exclusive, UTC timestamps,
/// at most 366 days; with neither given, the current UTC day.
/// </summary>
public sealed record DashboardDateRange(DateTimeOffset From, DateTimeOffset To)
{
    public static readonly TimeSpan MaxLength = TimeSpan.FromDays(366);

    public static ValidationResult<DashboardDateRange> Parse(DashboardQuery query, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new Dictionary<string, string[]>();

        if (query.From is null && query.To is null)
        {
            var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            return new(new DashboardDateRange(today, today.AddDays(1)), errors);
        }

        var from = Timestamp(query.From, "from", errors);
        var to = Timestamp(query.To, "to", errors);
        if (errors.Count == 0)
        {
            if (to <= from)
            {
                errors["to"] = ["'to' must be later than 'from'."];
            }
            else if (to - from > MaxLength)
            {
                errors["to"] = [$"The range cannot exceed {MaxLength.TotalDays:0} days."];
            }
        }

        return errors.Count > 0 ? new(null, errors) : new(new DashboardDateRange(from, to), errors);
    }

    private static DateTimeOffset Timestamp(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = ["Supply both 'from' and 'to', or neither for today."];
            return default;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            errors[field] = ["Use an ISO 8601 timestamp, for example 2026-11-01T00:00:00Z."];
            return default;
        }

        return parsed.ToUniversalTime();
    }
}

/// <summary>The shared admin/merchant delivery-rate formula.</summary>
public static class DeliveryRates
{
    public const int Decimals = 4;

    /// <summary>delivered/(delivered+failed) and failed/(delivered+failed), rounded half-up to 4 places; both 0 when nothing finished.</summary>
    public static (decimal Success, decimal Failure) Calculate(int delivered, int failed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delivered);
        ArgumentOutOfRangeException.ThrowIfNegative(failed);

        var terminal = delivered + failed;
        if (terminal == 0)
        {
            return (0m, 0m);
        }

        return (Round((decimal)delivered / terminal), Round((decimal)failed / terminal));
    }

    private static decimal Round(decimal value) => decimal.Round(value, Decimals, MidpointRounding.AwayFromZero);
}
