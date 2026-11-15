using System.Net;
using System.Text.Json;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierWorkspaceApiTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SummarySeparatesScheduledWorkAndOverdueAndNeverCountsOtherCouriers()
    {
        var actor = Guid.NewGuid();
        await using var today = await StatusTestScenario.CreateAsync(fixture, courierId: actor);
        await using var overdue = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.InTransit, courierId: actor, serviceDate: new(2026, 10, 27));
        await using var failed = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Failed, courierId: actor);
        await using var foreign = await StatusTestScenario.CreateAsync(fixture);
        using var response = await today.Client.GetAsync("/api/v1/couriers/me/summary?date=2026-10-28");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement;
        Assert.Equal("Asia/Colombo", body.GetProperty("timeZone").GetString());
        Assert.Equal(2, body.GetProperty("day").GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("day").GetProperty("assigned").GetInt32());
        Assert.Equal(1, body.GetProperty("day").GetProperty("failed").GetInt32());
        Assert.Equal(0, body.GetProperty("day").GetProperty("inTransit").GetInt32());
        Assert.Equal(1, body.GetProperty("overdueCount").GetInt32());
        Assert.Equal(2, body.GetProperty("queues").GetProperty("active").GetInt32());
        Assert.DoesNotContain("recipient", body.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StageSearchAndOverdueFiltersAreAppliedBeforePagination()
    {
        var actor = Guid.NewGuid();
        await using var first = await StatusTestScenario.CreateAsync(fixture, courierId: actor);
        await using var second = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.InTransit, courierId: actor);
        await using var third = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.InTransit, courierId: actor, serviceDate: new(2026, 10, 27));
        var path = "/api/v1/couriers/me/assignments?date=2026-10-28&status=InTransit&pageSize=1";
        using var response = await first.Client.GetAsync(path);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        using var overdue = await first.Client.GetAsync(path + "&overdueOnly=true");
        using var overdueBody = JsonDocument.Parse(await overdue.Content.ReadAsStringAsync());
        Assert.Equal(1, overdueBody.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(third.ParcelId, Assert.Single(overdueBody.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var tracking = (await second.GraphAsync()).Parcel.TrackingNumber;
        using var search = await first.Client.GetAsync(path + "&search=" + tracking);
        using var searchBody = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        Assert.Equal(1, searchBody.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(second.ParcelId, Assert.Single(searchBody.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        using var counts = await first.Client.GetAsync("/api/v1/couriers/me/summary?date=2026-10-28&overdueOnly=true");
        using var countsBody = JsonDocument.Parse(await counts.Content.ReadAsStringAsync());
        Assert.Equal(1, countsBody.RootElement.GetProperty("queues").GetProperty("inTransit").GetInt32());
    }

    [Fact]
    public async Task TerminalFiltersRetainMinimalHistoricalPrivacy()
    {
        var actor = Guid.NewGuid();
        await using var active = await StatusTestScenario.CreateAsync(fixture, courierId: actor);
        await using var failed = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Failed, courierId: actor);
        using var response = await active.Client.GetAsync("/api/v1/couriers/me/history?status=Failed&date=2026-10-28&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(failed.ParcelId, Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.DoesNotContain("recipient", json.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deliveryNote", json.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("summary?date=2026-02-30")]
    [InlineData("summary?userId=invalid")]
    [InlineData("summary?overdueOnly=1")]
    [InlineData("summary?date=2026-10-28&date=2026-10-29")]
    [InlineData("assignments?status=Delivered")]
    [InlineData("assignments?status=2")]
    [InlineData("assignments?search=" + "long%0Atext")]
    [InlineData("history?status=Assigned")]
    public async Task InvalidFiltersAreRejected(string query)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        using var response = await scenario.Client.GetAsync("/api/v1/couriers/me/" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Merchant")]
    [InlineData("Admin")]
    [InlineData("Dispatcher")]
    public async Task SummaryRequiresCourierBeforeReadingPrivateData(string role)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        using var factory = scenario.CreateFactory(role: role);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/couriers/me/summary?date=invalid");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
