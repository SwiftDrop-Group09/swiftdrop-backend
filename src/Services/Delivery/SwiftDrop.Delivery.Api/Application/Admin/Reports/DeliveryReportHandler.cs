using System.Globalization;
using System.Text.RegularExpressions;

namespace SwiftDrop.Delivery.Api.Application.Admin.Reports;

public sealed class DeliveryReportHandler(
    IDeliveryReportStore store,
    ICurrentDeliveryUser user)
{
    private static readonly Regex UtcTimestampPattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$",
        RegexOptions.CultureInvariant);

    public async Task<ReportResult<DeliveryReportResponse>> ExecuteAsync(
        string? fromStr,
        string? toStr,
        CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated)
        {
            return new(null, ReportFailure.Unauthenticated);
        }

        if (!user.IsAdmin)
        {
            return new(null, ReportFailure.Forbidden);
        }

        DateTimeOffset from;
        DateTimeOffset to;

        if (string.IsNullOrWhiteSpace(fromStr))
        {
            return new(null, ReportFailure.InvalidDateRange,
                new Dictionary<string, string[]> { ["from"] = ["'from' UTC timestamp is required."] });
        }
        if (!TryParseUtc(fromStr, out from))
        {
            return new(null, ReportFailure.InvalidDateRange,
                new Dictionary<string, string[]> { ["from"] = ["Invalid ISO 8601 UTC timestamp for 'from'."] });
        }

        if (string.IsNullOrWhiteSpace(toStr))
        {
            return new(null, ReportFailure.InvalidDateRange,
                new Dictionary<string, string[]> { ["to"] = ["'to' UTC timestamp is required."] });
        }
        if (!TryParseUtc(toStr, out to))
        {
            return new(null, ReportFailure.InvalidDateRange,
                new Dictionary<string, string[]> { ["to"] = ["Invalid ISO 8601 UTC timestamp for 'to'."] });
        }

        if (to <= from)
        {
            return new(null, ReportFailure.InvalidDateRange,
                new Dictionary<string, string[]> { ["to"] = ["'to' timestamp must be strictly after 'from' timestamp."] });
        }

        if ((to - from).TotalDays > 366)
        {
            return new(null, ReportFailure.InvalidDateRange,
                new Dictionary<string, string[]> { ["range"] = ["The maximum query date range is 366 days."] });
        }

        var report = await store.GenerateReportAsync(from, to, cancellationToken);
        return new(report);
    }

    private static bool TryParseUtc(string value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return UtcTimestampPattern.IsMatch(value)
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out timestamp)
            && timestamp.Offset == TimeSpan.Zero;
    }
}
