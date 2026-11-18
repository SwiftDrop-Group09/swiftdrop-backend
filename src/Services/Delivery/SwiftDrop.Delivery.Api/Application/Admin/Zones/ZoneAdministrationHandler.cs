using System.Globalization;
using System.Text.RegularExpressions;

namespace SwiftDrop.Delivery.Api.Application.Admin.Zones;

public sealed class ZoneAdministrationHandler(
    IZoneAdministrationStore store,
    ICurrentDeliveryUser user)
{
    private static readonly Regex UtcTimestampPattern = new(
        @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)\z",
        RegexOptions.CultureInvariant);

    public async Task<ZoneResult<ZonePage>> ListAsync(int? page, int? pageSize, CancellationToken cancellationToken, ZoneQuery? filters = null)
    {
        if (!user.IsAuthenticated)
        {
            return new(null, ZoneFailure.Unauthenticated);
        }

        if (!user.IsAdmin)
        {
            return new(null, ZoneFailure.Forbidden);
        }

        filters ??= new();
        if (filters.Search?.Length > 200 || filters.City?.Length > 200 || filters.NotCity?.Length > 200
            || filters.Status is not (null or "" or "active" or "inactive"))
            return new(null, ZoneFailure.InvalidInput);
        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 20, 1, 100);
        if ((long)(p - 1) * size > int.MaxValue) return new(null, ZoneFailure.InvalidInput);
        var result = await store.ListZonesAsync(p, size, cancellationToken, filters);
        return new(result);
    }

    public async Task<ZoneResult<ZoneDto>> CreateAsync(ZoneInput? input, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId is not { } actorId)
        {
            return new(null, ZoneFailure.Unauthenticated);
        }

        if (!user.IsAdmin)
        {
            return new(null, ZoneFailure.Forbidden);
        }

        if (input is null || string.IsNullOrWhiteSpace(input.Name))
        {
            return new(null, ZoneFailure.InvalidInput,
                new Dictionary<string, string[]> { ["name"] = ["Zone name is required."] });
        }

        return await store.CreateZoneAsync(input, actorId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<ZoneResult<ZoneDto>> UpdateAsync(Guid id, ZoneInput? input, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId is not { } actorId)
        {
            return new(null, ZoneFailure.Unauthenticated);
        }

        if (!user.IsAdmin)
        {
            return new(null, ZoneFailure.Forbidden);
        }

        if (id == Guid.Empty)
        {
            return new(null, ZoneFailure.NotFound);
        }

        if (input is null || string.IsNullOrWhiteSpace(input.Name))
        {
            return new(null, ZoneFailure.InvalidInput,
                new Dictionary<string, string[]> { ["name"] = ["Zone name is required."] });
        }

        return await store.UpdateZoneAsync(id, input, actorId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<ZoneResult<ZoneSummaryResponse>> GetSummaryAsync(string? from, string? to, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated)
        {
            return new(null, ZoneFailure.Unauthenticated);
        }

        if (!user.IsAdmin && !user.IsOperations)
        {
            return new(null, ZoneFailure.Forbidden);
        }

        // No bounds is the all-booking-dates catalog used by operations consumers.
        DateTimeOffset? parsedFrom = null;
        DateTimeOffset? parsedTo = null;
        if (from is not null || to is not null)
        {
            var errors = new Dictionary<string, string[]>();
            if (!TryParseUtc(from, out var start))
                errors["from"] = ["'from' must be an ISO 8601 UTC timestamp; supply both bounds or omit both."];
            if (!TryParseUtc(to, out var end))
                errors["to"] = ["'to' must be an ISO 8601 UTC timestamp; supply both bounds or omit both."];
            if (errors.Count > 0)
                return new(null, ZoneFailure.InvalidDateRange, errors);
            if (end <= start)
                return new(null, ZoneFailure.InvalidDateRange,
                    new Dictionary<string, string[]> { ["to"] = ["'to' must be strictly after 'from'."] });
            if ((end - start).TotalDays > 366)
                return new(null, ZoneFailure.InvalidDateRange,
                    new Dictionary<string, string[]> { ["range"] = ["The maximum query date range is 366 days."] });
            parsedFrom = start;
            parsedTo = end;
        }

        var result = await store.GetZoneSummaryAsync(parsedFrom, parsedTo, cancellationToken);
        return new(result);
    }

    private static bool TryParseUtc(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return value is not null && UtcTimestampPattern.IsMatch(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp)
            && timestamp.Offset == TimeSpan.Zero;
    }
}
