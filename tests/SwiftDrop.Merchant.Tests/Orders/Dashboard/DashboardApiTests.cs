using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftDrop.Merchant.Api.Application.Orders.Dashboard;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Tests.Authentication;
using static SwiftDrop.Merchant.Tests.Orders.Processing.ProcessingTestData;

namespace SwiftDrop.Merchant.Tests.Orders.Dashboard;

/// <summary>TC-M06 over HTTP: the owner's dashboard is computed from their own orders only.</summary>
public sealed class DashboardApiTests
{
    private const string Path = "/api/v1/merchants/me/dashboard";
    private const string November = "?from=2026-11-01T00:00:00Z&to=2026-11-14T00:00:00Z";
    private static readonly DateTimeOffset Nov5 = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Owner_SeesOwnCountsRatesAndRecentOrders_NeverAnotherBusiness()
    {
        using var host = new OrderAuthTestHost(MerchantApiFactory.Merchant());
        var own = await host.SeedBusinessAsync(BusinessStatus.Approved, MerchantApiFactory.MerchantId);
        var other = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var id = own.Id;

        // Intake states
        await host.SeedJobAsync(Job(id, "A-ACCEPTED", Nov5));
        var retrying = Job(id, "A-RETRYING", Nov5.AddMinutes(1));
        retrying.ScheduleRetry("delivery_timeout", Nov5.AddMinutes(1));
        await host.SeedJobAsync(retrying);
        var failedJob = Job(id, "A-FAILED", Nov5.AddMinutes(2));
        failedJob.MarkFailed("pricing_changed", Nov5.AddMinutes(2));
        await host.SeedJobAsync(failedJob);

        // Parcels: 2 delivered, 1 failed, 1 cancelled (terminal in range), 1 in transit, 1 assigned
        await host.SeedJobAsync(CreatedJob(id, "A-D1", Nov5.AddMinutes(3), ParcelStatuses.Delivered, 6, Nov5.AddDays(1)));
        await host.SeedJobAsync(CreatedJob(id, "A-D2", Nov5.AddMinutes(4), ParcelStatuses.Delivered, 6, Nov5.AddDays(2)));
        await host.SeedJobAsync(CreatedJob(id, "A-F1", Nov5.AddMinutes(5), ParcelStatuses.Failed, 5, Nov5.AddDays(2)));
        await host.SeedJobAsync(CreatedJob(id, "A-C1", Nov5.AddMinutes(6), ParcelStatuses.Cancelled, 2, Nov5.AddDays(3)));
        await host.SeedJobAsync(CreatedJob(id, "A-T1", Nov5.AddMinutes(7), ParcelStatuses.InTransit, 4,
            refreshedAt: new DateTimeOffset(2026, 11, 13, 9, 40, 12, TimeSpan.Zero)));
        await host.SeedJobAsync(CreatedJob(id, "A-S1", Nov5.AddMinutes(8), ParcelStatuses.Assigned, 2));

        // Another business: must not leak into any figure
        await host.SeedJobAsync(CreatedJob(other.Id, "B-D1", Nov5, ParcelStatuses.Delivered, 6, Nov5.AddDays(1)));
        await host.SeedJobAsync(Job(other.Id, "B-ACCEPTED", Nov5));

        var dashboard = await GetDashboardAsync(host, November);

        Assert.Equal(9, dashboard.TotalOrders);
        Assert.Equal(2, dashboard.PendingCount);
        Assert.Equal(1, dashboard.FailedJobs);
        Assert.Equal(2, dashboard.StatusCounts[ParcelStatuses.Delivered]);
        Assert.Equal(1, dashboard.StatusCounts[ParcelStatuses.Failed]);
        Assert.Equal(1, dashboard.StatusCounts[ParcelStatuses.Cancelled]);
        Assert.Equal(1, dashboard.StatusCounts[ParcelStatuses.InTransit]);
        Assert.Equal(1, dashboard.StatusCounts[ParcelStatuses.Assigned]);
        Assert.Equal(0, dashboard.StatusCounts[ParcelStatuses.OutForDelivery]);
        Assert.Equal(8, dashboard.StatusCounts.Count);
        Assert.Equal(2, dashboard.ActiveCount);
        Assert.Equal(2, dashboard.TerminalDeliveredCount);
        Assert.Equal(1, dashboard.TerminalFailedCount);
        Assert.Equal(1, dashboard.TerminalCancelledCount);
        Assert.Equal(0.6667m, dashboard.SuccessRate);
        Assert.Equal(0.3333m, dashboard.FailureRate);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 9, 40, 12, TimeSpan.Zero), dashboard.LastRefreshedAt);
        Assert.Equal(5, dashboard.RecentOrders.Count);
        Assert.Equal("A-S1", dashboard.RecentOrders[0].ExternalOrderId);
        Assert.DoesNotContain(dashboard.RecentOrders, order => order.ExternalOrderId.StartsWith("B-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoFinishedParcels_GivesZeroRates()
    {
        using var host = new OrderAuthTestHost(MerchantApiFactory.Merchant());
        var own = await host.SeedBusinessAsync(BusinessStatus.Approved, MerchantApiFactory.MerchantId);
        await host.SeedJobAsync(CreatedJob(own.Id, "A-T1", Nov5, ParcelStatuses.InTransit, 3));

        var dashboard = await GetDashboardAsync(host, November);

        Assert.Equal((0m, 0m), (dashboard.SuccessRate, dashboard.FailureRate));
        Assert.Equal(1, dashboard.ActiveCount);
    }

    [Fact]
    public async Task TerminalOutcomesAreCountedByWhenTheyHappened()
    {
        using var host = new OrderAuthTestHost(MerchantApiFactory.Merchant());
        var own = await host.SeedBusinessAsync(BusinessStatus.Approved, MerchantApiFactory.MerchantId);
        // Ordered in October, delivered in November: a November outcome, not a November order.
        await host.SeedJobAsync(CreatedJob(own.Id, "A-OCT", Nov5.AddDays(-20), ParcelStatuses.Delivered, 6, Nov5));
        // Ordered in November, delivered after the range: a November order, not a November outcome.
        await host.SeedJobAsync(CreatedJob(own.Id, "A-LATE", Nov5, ParcelStatuses.Delivered, 6, Nov5.AddDays(20)));

        var dashboard = await GetDashboardAsync(host, November);

        Assert.Equal(1, dashboard.TotalOrders);
        Assert.Equal(1, dashboard.TerminalDeliveredCount);
        Assert.Equal(1m, dashboard.SuccessRate);
    }

    [Fact]
    public async Task WithoutDates_ShowsTodayAndIsNeverCached()
    {
        using var host = new OrderAuthTestHost(MerchantApiFactory.Merchant());
        await host.SeedBusinessAsync(BusinessStatus.Approved, MerchantApiFactory.MerchantId);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri(Path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var dashboard = (await response.Content.ReadFromJsonAsync<MerchantDashboardResponse>(JsonSerializerOptions.Web))!;
        Assert.Equal(TimeSpan.FromDays(1), dashboard.To - dashboard.From);
        Assert.Equal(TimeSpan.Zero, dashboard.From.TimeOfDay);
    }

    [Theory]
    [InlineData("?from=2026-01-01T00:00:00Z&to=2027-01-03T00:00:00Z")]
    [InlineData("?from=2026-11-14T00:00:00Z&to=2026-11-01T00:00:00Z")]
    [InlineData("?from=2026-11-01T00:00:00Z")]
    [InlineData("?from=last-week&to=2026-11-14T00:00:00Z")]
    public async Task InvalidRange_Returns400(string query)
    {
        using var host = new OrderAuthTestHost(MerchantApiFactory.Merchant());
        await host.SeedBusinessAsync(BusinessStatus.Approved, MerchantApiFactory.MerchantId);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri(Path + query, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Suspended)]
    public async Task OwnerOfUnapprovedBusiness_Returns403(BusinessStatus status)
    {
        using var host = new OrderAuthTestHost(MerchantApiFactory.Merchant());
        await host.SeedBusinessAsync(status, MerchantApiFactory.MerchantId);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri(Path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ApiKey_CannotOpenTheDashboard()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: [ApiKeyScopes.OrdersRead, ApiKeyScopes.OrdersWrite]);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);

        using var response = await client.GetAsync(new Uri(Path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<MerchantDashboardResponse> GetDashboardAsync(OrderAuthTestHost host, string query)
    {
        using var client = host.CreateClient();
        using var response = await client.GetAsync(new Uri(Path + query, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MerchantDashboardResponse>(JsonSerializerOptions.Web))!;
    }
}
