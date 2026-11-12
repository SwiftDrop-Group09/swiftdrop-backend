using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.Admin.Reports;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.Admin;

public sealed class PostgresDeliveryReportStore(Func<DeliveryDbContext> database) : IDeliveryReportStore
{
    public async Task<DeliveryReportResponse> GenerateReportAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var db = database();

        var terminalStatuses = new[] { ParcelStatus.Delivered, ParcelStatus.Failed, ParcelStatus.Cancelled };

        var records = await db.BookingHistory.AsNoTracking()
            .Where(h => h.Timestamp >= from && h.Timestamp < to && terminalStatuses.Contains(h.ToStatus))
            .Select(h => new { h.ToStatus, h.Timestamp })
            .ToListAsync(cancellationToken);

        var daysMap = new SortedDictionary<string, (int Delivered, int Failed, int Cancelled)>(StringComparer.Ordinal);

        // Pre-seed calendar days between from and to if range is <= 31 days for clean charting,
        // or just populate active transition days.
        var curDate = from.UtcDateTime.Date;
        var endDate = to.UtcDateTime.Date;
        if ((endDate - curDate).TotalDays <= 31)
        {
            while (curDate <= endDate && curDate < to.UtcDateTime)
            {
                daysMap[curDate.ToString("yyyy-MM-dd")] = (0, 0, 0);
                curDate = curDate.AddDays(1);
            }
        }

        foreach (var r in records)
        {
            var dateKey = r.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
            daysMap.TryGetValue(dateKey, out var current);

            if (r.ToStatus == ParcelStatus.Delivered)
            {
                daysMap[dateKey] = (current.Delivered + 1, current.Failed, current.Cancelled);
            }
            else if (r.ToStatus == ParcelStatus.Failed)
            {
                daysMap[dateKey] = (current.Delivered, current.Failed + 1, current.Cancelled);
            }
            else if (r.ToStatus == ParcelStatus.Cancelled)
            {
                daysMap[dateKey] = (current.Delivered, current.Failed, current.Cancelled + 1);
            }
        }

        var days = daysMap.Select(kv => new DailyReportMetric(kv.Key, kv.Value.Delivered, kv.Value.Failed, kv.Value.Cancelled)).ToList();

        var totalDelivered = records.Count(r => r.ToStatus == ParcelStatus.Delivered);
        var totalFailed = records.Count(r => r.ToStatus == ParcelStatus.Failed);
        var totalCancelled = records.Count(r => r.ToStatus == ParcelStatus.Cancelled);
        var terminalCount = totalDelivered + totalFailed;

        var successRate = terminalCount > 0
            ? decimal.Round((decimal)totalDelivered / terminalCount, 4, MidpointRounding.AwayFromZero)
            : 0m;

        var failureRate = terminalCount > 0
            ? decimal.Round((decimal)totalFailed / terminalCount, 4, MidpointRounding.AwayFromZero)
            : 0m;

        return new DeliveryReportResponse(
            From: from,
            To: to,
            Delivered: totalDelivered,
            Failed: totalFailed,
            Cancelled: totalCancelled,
            TerminalCount: terminalCount,
            SuccessRate: successRate,
            FailureRate: failureRate,
            Days: days);
    }
}
