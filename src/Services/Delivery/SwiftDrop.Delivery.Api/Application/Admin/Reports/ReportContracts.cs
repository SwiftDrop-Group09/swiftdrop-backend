namespace SwiftDrop.Delivery.Api.Application.Admin.Reports;

public enum ReportFailure
{
    Unauthenticated,
    Forbidden,
    InvalidDateRange
}

public sealed record DailyReportMetric(
    string Date,
    int Delivered,
    int Failed,
    int Cancelled);

public sealed record DeliveryReportResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    int Delivered,
    int Failed,
    int Cancelled,
    int TerminalCount,
    decimal SuccessRate,
    decimal FailureRate,
    IReadOnlyList<DailyReportMetric> Days);

public sealed record ReportResult<T>(T? Response, ReportFailure? Failure = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
