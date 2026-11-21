using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Courier;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Dispatch;

[Collection("Postgres")]
public sealed class ReassignmentOptionsTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("?reassign=")]
    [InlineData("?reassign=True")]
    [InlineData("?reassign=1")]
    [InlineData("?reassign=%20true")]
    [InlineData("?reassign=true%20")]
    [InlineData("?reassign=true&reassign=false")]
    [InlineData("?Reassign=true")]
    [InlineData("?reassign=true&other=x")]
    public async Task BooleanQueryIsStrictAndRejectedBeforeStorage(string query)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), "Dispatcher"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/deliveries/{Guid.NewGuid()}/assignment-options{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("validation", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("2026-10-28", 0, "2026-10-28")]
    [InlineData("2026-10-30", 0, "2026-10-30")]
    [InlineData("2026-10-28", 1, "2026-10-29")]
    public async Task ContextDefaultsAndReplacementCandidatesShareTheRead(string originalDate, int advanceDays, string selectedDate)
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        using (var ordinary = await client.GetAsync($"/api/v1/deliveries/{s.ParcelId}/assignment-options?reassign=false"))
        {
            using var json = JsonDocument.Parse(await ordinary.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("reassign").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("currentAssignment").ValueKind);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deliveries/assign")
        { Content = JsonContent.Create(new { parcelId = s.ParcelId, courierId = s.CourierId, expectedVersion = 1, serviceDate = originalDate }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var assigned = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(advanceDays);
        using var response = await client.GetAsync($"/api/v1/deliveries/{s.ParcelId}/assignment-options?reassign=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.True(root.GetProperty("reassign").GetBoolean());
        Assert.Equal(2, root.GetProperty("parcelVersion").GetInt64());
        Assert.Equal(selectedDate, root.GetProperty("serviceDate").GetString());
        var current = root.GetProperty("currentAssignment");
        Assert.Equal(new[] { "courierCode", "courierId", "serviceDate" }, current.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(s.CourierId, current.GetProperty("courierId").GetGuid());
        await using var stored = s.Context();
        var number = await stored.Couriers.Where(x => x.Id == s.CourierId).Select(x => x.BusinessNumber).SingleAsync();
        Assert.Equal("COU-" + number.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
            current.GetProperty("courierCode").GetString());
        Assert.Equal(originalDate, current.GetProperty("serviceDate").GetString());
        Assert.Equal(s.OtherCourierId, Assert.Single(root.GetProperty("items").EnumerateArray()).GetProperty("courierId").GetGuid());
        Assert.Equal(1, root.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData(ParcelStatus.Created)]
    [InlineData(ParcelStatus.PickedUp)]
    [InlineData(ParcelStatus.Failed)]
    public async Task InvalidReassignmentContextIsConflict(ParcelStatus status)
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture, status);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Admin");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/deliveries/{s.ParcelId}/assignment-options?reassign=true");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Courier")]
    [InlineData("Merchant")]
    [InlineData(null)]
    public async Task AuthorizationStillPrecedesQueryValidation(string? role)
    {
        using var factory = new QuoteApiFactory(null, role is null ? null : QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/deliveries/{Guid.NewGuid()}/assignment-options?reassign=bad");
        Assert.Equal(role is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PagingAndDutyUseTheReplacementDateAndIgnoreOldCouriersActivity()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        string currentCode;
        await using (var db = s.Context())
        {
            var current = await db.Couriers.SingleAsync(x => x.Id == s.CourierId);
            currentCode = "COU-" + current.BusinessNumber.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            current.Configure(false, false, 1);
            (await db.Couriers.SingleAsync(x => x.Id == s.OtherCourierId)).Configure(true, false, 1);
            await db.SaveChangesAsync();
        }
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        foreach (var future in new[] { false, true })
        {
            using var response = await client.GetAsync($"/api/v1/deliveries/{s.ParcelId}/assignment-options?reassign=true&pageSize=1&serviceDate=" + (future ? "2026-10-29" : "2026-10-28"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(currentCode, body.RootElement.GetProperty("currentAssignment").GetProperty("courierCode").GetString());
            Assert.Equal(future ? 1 : 0, body.RootElement.GetProperty("totalCount").GetInt32());
        }
    }
}
