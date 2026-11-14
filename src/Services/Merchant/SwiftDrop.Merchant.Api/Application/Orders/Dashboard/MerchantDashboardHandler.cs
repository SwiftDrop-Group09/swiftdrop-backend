using Microsoft.EntityFrameworkCore;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Api.Application.Orders.Dashboard;

/// <summary>
/// FR-M06: dashboard figures computed only from this business's
/// <c>inbox_jobs</c> — never a live Delivery call at read time. Counting is
/// pushed to the database as grouped counts; only the five recent orders are loaded.
/// </summary>
public sealed class MerchantDashboardHandler(
    MerchantDbContext database,
    ICurrentOrderCaller currentCaller,
    TimeProvider clock)
{
    public const int RecentOrderCount = 5;

    public async Task<OrderResult<MerchantDashboardResponse>> GetAsync(DashboardQuery query, CancellationToken cancellationToken)
    {
        var caller = await currentCaller.ResolveAsync(cancellationToken);
        if (!caller.CanViewDashboard || caller.BusinessId is not { } businessId)
        {
            return OrderResult<MerchantDashboardResponse>.Fail(OrderFailure.Forbidden);
        }

        var range = DashboardDateRange.Parse(query, clock.GetUtcNow());
        if (!range.IsValid)
        {
            return OrderResult<MerchantDashboardResponse>.Fail(OrderFailure.InvalidInput, range.Errors);
        }

        var (from, to) = range.Value!;
        var businessJobs = database.InboxJobs.AsNoTracking().Where(job => job.BusinessId == businessId);
        var createdInRange = businessJobs.Where(job => job.CreatedAt >= from && job.CreatedAt < to);

        // 1. Orders created in range, by intake state.
        var byState = await createdInRange
            .GroupBy(job => job.State)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.State, row => row.Count, cancellationToken);

        // 2. Of those, parcels by their current (refreshed) status.
        var byParcelStatus = await createdInRange
            .Where(job => job.State == InboxJobState.Created && job.ParcelStatus != null)
            .GroupBy(job => job.ParcelStatus!)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancellationToken);

        // 3. Terminal outcomes that happened in range (admin-report formula).
        var terminalInRange = await businessJobs
            .Where(job => job.State == InboxJobState.Created && job.TerminalAt >= from && job.TerminalAt < to && job.ParcelStatus != null)
            .GroupBy(job => job.ParcelStatus!)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancellationToken);

        var lastRefreshedAt = await businessJobs
            .Where(job => job.LastRefreshedAt != null)
            .MaxAsync(job => job.LastRefreshedAt, cancellationToken);

        var recent = await createdInRange
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id)
            .Take(RecentOrderCount)
            .ToListAsync(cancellationToken);

        var statusCounts = ParcelStatuses.All.ToDictionary(status => status, status => byParcelStatus.GetValueOrDefault(status));
        var delivered = terminalInRange.GetValueOrDefault(ParcelStatuses.Delivered);
        var failed = terminalInRange.GetValueOrDefault(ParcelStatuses.Failed);
        var (successRate, failureRate) = DeliveryRates.Calculate(delivered, failed);

        return OrderResult<MerchantDashboardResponse>.Ok(new MerchantDashboardResponse(
            From: from,
            To: to,
            TotalOrders: byState.Values.Sum(),
            PendingCount: byState.GetValueOrDefault(InboxJobState.Accepted) + byState.GetValueOrDefault(InboxJobState.Retrying),
            FailedJobs: byState.GetValueOrDefault(InboxJobState.Failed),
            StatusCounts: statusCounts,
            ActiveCount: statusCounts.Where(pair => !ParcelStatuses.IsTerminal(pair.Key)).Sum(pair => pair.Value),
            TerminalDeliveredCount: delivered,
            TerminalFailedCount: failed,
            TerminalCancelledCount: terminalInRange.GetValueOrDefault(ParcelStatuses.Cancelled),
            SuccessRate: successRate,
            FailureRate: failureRate,
            LastRefreshedAt: lastRefreshedAt,
            RecentOrders: recent.Select(OrderJobMapper.ToResponse).ToList()));
    }
}
