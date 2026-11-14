namespace SwiftDrop.Merchant.Api.Application.Orders.Dashboard;

/// <summary>Raw query string values (DateQuery); parsed by <see cref="DashboardDateRange"/>.</summary>
public sealed record DashboardQuery(string? From, string? To);

/// <summary>
/// MerchantDashboard. Two scopes, both for the caller's business only:
/// <list type="bullet">
/// <item><b>Orders created in [from, to)</b>: totalOrders, pendingCount, failedJobs, statusCounts, activeCount, recentOrders.</item>
/// <item><b>Terminal outcomes timestamped in [from, to)</b>: terminal*Count and the rates — the same formula as
/// the admin delivery report: rate = count / (delivered + failed), both 0 when nothing finished; Cancelled excluded.</item>
/// </list>
/// Parcel data is Merchant's refreshed copy of Delivery's state; <see cref="LastRefreshedAt"/> says how fresh it is.
/// </summary>
public sealed record MerchantDashboardResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    int TotalOrders,
    int PendingCount,
    int FailedJobs,
    IReadOnlyDictionary<string, int> StatusCounts,
    int ActiveCount,
    int TerminalDeliveredCount,
    int TerminalFailedCount,
    int TerminalCancelledCount,
    decimal SuccessRate,
    decimal FailureRate,
    DateTimeOffset? LastRefreshedAt,
    IReadOnlyList<OrderJobResponse> RecentOrders);
