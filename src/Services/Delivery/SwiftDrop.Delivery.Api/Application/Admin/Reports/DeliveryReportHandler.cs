namespace SwiftDrop.Delivery.Api.Application.Admin.Reports;

public sealed class DeliveryReportHandler(
    IDeliveryReportStore store,
    ICurrentDeliveryUser user)
{
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

        var now = DateTimeOffset.UtcNow;
        var startOfToday = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

        if (string.IsNullOrWhiteSpace(fromStr))
        {
            from = startOfToday;
        }
        else if (!DateTimeOffset.TryParse(fromStr, out from))
        {
            return new(null, ReportFailure.InvalidDateRange,
                new Dictionary<string, string[]> { ["from"] = ["Invalid ISO 8601 UTC timestamp for 'from'."] });
        }

        if (string.IsNullOrWhiteSpace(toStr))
        {
            to = startOfToday.AddDays(1);
        }
        else if (!DateTimeOffset.TryParse(toStr, out to))
        {
            return new(null, ReportFailure.InvalidDateRange,
                new Dictionary<string, string[]> { ["to"] = ["Invalid ISO 8601 UTC timestamp for 'to'."] });
        }

        from = from.ToUniversalTime();
        to = to.ToUniversalTime();

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
}

