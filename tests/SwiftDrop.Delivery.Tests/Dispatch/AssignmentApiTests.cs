using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Courier;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Dispatch;

[Collection("Postgres")]
public sealed class AssignmentApiTests(PostgresFixture fixture)
{
    private const string Path = "/api/v1/deliveries/assign";
    private static string Options(Guid id, string query = "") => $"/api/v1/deliveries/{id}/assignment-options{query}";
    private static async Task<HttpResponseMessage> Assign(HttpClient client, Guid parcel, Guid courier, string? key = null,
        long version = 1, string? date = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        { Content = JsonContent.Create(new { parcelId = parcel, courierId = courier, expectedVersion = version, reassign = false, serviceDate = date }) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("Dispatcher")]
    [InlineData("Admin")]
    public async Task AssignmentCommitsOnceAndUpdatesQueueWorkloadManifestAndPrivateHistory(string role)
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        var actor = Guid.NewGuid();
        using var factory = s.CreateFactory(actor, role);
        using var client = factory.CreateClient();
        using var options = await client.GetAsync(Options(s.ParcelId));
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        using var choices = JsonDocument.Parse(await options.Content.ReadAsStringAsync());
        Assert.Equal("2026-10-28", choices.RootElement.GetProperty("businessToday").GetString());
        Assert.Equal(2, choices.RootElement.GetProperty("totalCount").GetInt32());
        Assert.True(options.Headers.CacheControl?.NoStore);
        var key = Guid.NewGuid().ToString();
        using var response = await Assign(client, s.ParcelId, s.CourierId, key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        using var result = JsonDocument.Parse(body);
        Assert.Equal("Assigned", result.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, result.RootElement.GetProperty("version").GetInt64());
        var graph = await s.GraphAsync();
        Assert.Equal(ParcelStatus.Assigned, graph.Parcel.Status);
        Assert.Equal(actor, graph.History.Last().ChangedBy);
        Assert.Equal(new DateOnly(2026, 10, 28), Assert.Single(graph.Assignments).ServiceDate);
        Assert.Equal(2, graph.Events.Length);
        using var replay = await Assign(client, s.ParcelId, s.CourierId, key);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.Equal(graph.Signature(), (await s.GraphAsync()).Signature());
        using var queue = await client.GetAsync("/api/v1/parcels?status=Created");
        Assert.DoesNotContain(s.ParcelId.ToString(), await queue.Content.ReadAsStringAsync());
        using var manifest = await s.Client.GetAsync("/api/v1/couriers/me/assignments");
        Assert.Contains(s.ParcelId.ToString(), await manifest.Content.ReadAsStringAsync());
        using var workload = await client.GetAsync($"/api/v1/couriers/{s.CourierId}/assignments");
        Assert.Contains(s.ParcelId.ToString(), await workload.Content.ReadAsStringAsync());
        using var pickup = await s.UpdateAsync("PickedUp", 2);
        Assert.Equal(HttpStatusCode.OK, pickup.StatusCode);
        using var failure = await s.UpdateAsync("Failed", 3, reason: "Other");
        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        using var finished = await client.GetAsync($"/api/v1/couriers/{s.CourierId}/assignments");
        Assert.DoesNotContain(s.ParcelId.ToString(), await finished.Content.ReadAsStringAsync());
        using var afterFinish = await Assign(client, s.ParcelId, s.CourierId, key);
        Assert.Equal(body, await afterFinish.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Courier")]
    [InlineData("Merchant")]
    [InlineData(null)]
    public async Task AuthorizationPrecedesValidationAndUnavailableStorage(string? role)
    {
        using var factory = new QuoteApiFactory(null, role is null ? null : QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent("not json") };
        using var response = await client.SendAsync(request);
        Assert.Equal(role is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        using var read = await client.GetAsync(Options(Guid.NewGuid(), "?unknown=bad"));
        Assert.Equal(response.StatusCode, read.StatusCode);
    }

    [Theory]
    [InlineData("{\"parcelId\":\"bad\"}")]
    [InlineData("{\"parcelId\":null,\"parcelId\":null}")]
    [InlineData("{\"role\":\"Admin\"}")]
    [InlineData("{\"expectedVersion\":1.5}")]
    [InlineData("{\"expectedVersion\":\"1\"}")]
    public async Task MalformedBodiesDoNotReachStorage(string body)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), "Dispatcher"));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", "validation-key");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?page=1&page=2")]
    [InlineData("?serviceDate=2026-02-30")]
    [InlineData("?unknown=1")]
    public async Task InvalidReadQueryDoesNotReachStorage(string query)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), "Dispatcher"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Options(Guid.NewGuid(), query));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(false, true, "2026-10-28", 422)]
    [InlineData(true, false, "2026-10-28", 422)]
    [InlineData(true, false, "2026-10-29", 200)]
    [InlineData(false, false, "2026-10-29", 422)]
    [InlineData(true, true, "2026-10-27", 400)]
    public async Task DutyAndSchedulingPolicyIsEnforcedByReadAndWrite(bool active, bool duty, string date, int expected)
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        await using (var db = s.Context())
        {
            var courier = await db.Couriers.SingleAsync(x => x.Id == s.CourierId);
            courier.Configure(active, duty, 20);
            await db.SaveChangesAsync();
        }
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        using var options = await client.GetAsync(Options(s.ParcelId, "?serviceDate=" + date));
        if (expected == 400) Assert.Equal(HttpStatusCode.BadRequest, options.StatusCode);
        else
        {
            using var document = JsonDocument.Parse(await options.Content.ReadAsStringAsync());
            Assert.Equal(expected == 200, document.RootElement.GetProperty("items").EnumerateArray()
                .Any(x => x.GetProperty("courierId").GetGuid() == s.CourierId));
        }
        using var response = await Assign(client, s.ParcelId, s.CourierId, date: date);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        if (expected != 200) Assert.Empty((await s.GraphAsync()).Assignments);
    }

    [Fact]
    public async Task StaleVersionChangedKeyBodyAndExplicitReassignmentAreRejected()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        using var stale = await Assign(client, s.ParcelId, s.CourierId, version: 2);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("stale_version", await stale.Content.ReadAsStringAsync());
        var key = Guid.NewGuid().ToString();
        using var first = await Assign(client, s.ParcelId, s.CourierId, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var changed = await Assign(client, s.ParcelId, s.OtherCourierId, key);
        Assert.Contains("idempotency_conflict", await changed.Content.ReadAsStringAsync());
        using var duplicate = await Assign(client, s.ParcelId, s.CourierId);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        { Content = JsonContent.Create(new { parcelId = s.ParcelId, courierId = s.OtherCourierId, expectedVersion = 2, reassign = true }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var reassign = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, reassign.StatusCode);
        Assert.Single((await s.GraphAsync()).Assignments);
    }

    [Fact]
    public async Task RetriedDefaultDateAndExactLargeVersionSurviveMidnightAndEligibilityChanges()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        const long version = 9007199254740993;
        await using (var db = s.Context())
        {
            var parcel = await db.Parcels.SingleAsync(x => x.Id == s.ParcelId);
            db.Entry(parcel).Property(x => x.Version).CurrentValue = version;
            await db.SaveChangesAsync();
        }
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        using var response = await Assign(client, s.ParcelId, s.CourierId, key, version);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("9007199254740994", body);
        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(1);
        await using (var db = s.Context())
        {
            (await db.Couriers.SingleAsync(x => x.Id == s.CourierId)).Configure(false, false, 1);
            await db.SaveChangesAsync();
        }
        using var replay = await Assign(client, s.ParcelId, s.CourierId, key, version);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.Equal(new DateOnly(2026, 10, 28), Assert.Single((await s.GraphAsync()).Assignments).ServiceDate);
    }

    [Fact]
    public async Task MissingAndForeignZoneCouriersCannotCommitAndLockTimeoutCanRetry()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        using var missing = await Assign(client, s.ParcelId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await using (var db = s.Context())
        {
            db.CourierZones.RemoveRange(await db.CourierZones.Where(x => x.CourierId == s.OtherCourierId).ToArrayAsync());
            await db.SaveChangesAsync();
        }
        using var zone = await Assign(client, s.ParcelId, s.OtherCourierId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, zone.StatusCode);
        var before = (await s.GraphAsync()).Signature();
        var key = Guid.NewGuid().ToString();
        await using (var holder = s.Context())
        await using (var transaction = await holder.Database.BeginTransactionAsync())
        {
            await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM couriers WHERE id = {s.CourierId} FOR UPDATE");
            using var blocked = await Assign(client, s.ParcelId, s.CourierId, key);
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), blocked.Headers.RetryAfter?.Delta);
        }
        Assert.Equal(before, (await s.GraphAsync()).Signature());
        using var retry = await Assign(client, s.ParcelId, s.CourierId, key);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentParcelsSerializeDailyCapacityButSeparateDatesHaveSeparateSlots(bool differentDates)
    {
        await using var a = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        await using var b = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        await using (var db = a.Context())
        {
            (await db.Couriers.SingleAsync(x => x.Id == a.CourierId)).Configure(true, true, 1);
            var zone = (await db.Parcels.SingleAsync(x => x.Id == b.ParcelId)).ZoneId;
            db.CourierZones.Add(new CourierZone(a.CourierId, zone));
            await db.SaveChangesAsync();
        }
        using var factory = a.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var responses = await Task.WhenAll(Assign(client, a.ParcelId, a.CourierId, date: "2026-10-28"),
            Assign(client, b.ParcelId, a.CourierId, date: differentDates ? "2026-10-29" : "2026-10-28"));
        try
        {
            Assert.Equal(differentDates ? 2 : 1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
            if (!differentDates) Assert.Single(responses, x => x.StatusCode == HttpStatusCode.UnprocessableEntity);
            await using var db = a.Context();
            var counts = await db.Assignments.Where(x => x.CourierId == a.CourierId && x.Status == AssignmentStatus.Active)
                .GroupBy(x => x.ServiceDate).Select(x => x.Count()).ToArrayAsync();
            Assert.All(counts, count => Assert.Equal(1, count));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameParcelRaceOrIdenticalRetryHasOnlyOneAssignment(bool sameKey)
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(Assign(client, s.ParcelId, s.CourierId, key),
            Assign(client, s.ParcelId, s.CourierId, sameKey ? key : null));
        try
        {
            Assert.Equal(sameKey ? 2 : 1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
            if (sameKey) Assert.Equal(await responses[0].Content.ReadAsStringAsync(), await responses[1].Content.ReadAsStringAsync());
            Assert.Single((await s.GraphAsync()).Assignments);
            Assert.Equal(2, (await s.GraphAsync()).Events.Length);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task EligibilityPagesAreStableAndZoneMembershipsDoNotMultiplyCandidates()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        await using (var db = s.Context())
        {
            var zone = (await db.Parcels.SingleAsync(x => x.Id == s.ParcelId)).ZoneId;
            var another = new DeliveryZone(Guid.NewGuid(), "Extra membership");
            db.Zones.Add(another);
            for (var i = 0; i < 23; i++)
            {
                var id = Guid.NewGuid();
                db.Couriers.Add(new Api.Domain.Courier(id, true, true, 1));
                db.CourierZones.AddRange(new CourierZone(id, zone), new CourierZone(id, another.Id));
            }
            await db.SaveChangesAsync();
        }
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        using var first = await client.GetAsync(Options(s.ParcelId, "?page=1"));
        using var second = await client.GetAsync(Options(s.ParcelId, "?page=2"));
        using var repeated = await client.GetAsync(Options(s.ParcelId, "?page=1"));
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.Equal(firstBody, await repeated.Content.ReadAsStringAsync());
        using var a = JsonDocument.Parse(firstBody);
        using var b = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(25, a.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(20, a.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(5, b.RootElement.GetProperty("items").GetArrayLength());
        var ids = a.RootElement.GetProperty("items").EnumerateArray().Concat(b.RootElement.GetProperty("items").EnumerateArray())
            .Select(x => x.GetProperty("courierId").GetGuid()).ToArray();
        Assert.Equal(ids.Order(), ids);
        Assert.Equal(25, ids.Distinct().Count());
        var items = a.RootElement.GetProperty("items").EnumerateArray()
            .Concat(b.RootElement.GetProperty("items").EnumerateArray()).ToArray();
        await using var stored = s.Context();
        var numbers = await stored.Couriers.Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.BusinessNumber);
        var codes = items.ToDictionary(x => x.GetProperty("courierId").GetGuid(),
            x => x.GetProperty("courierCode").GetString());
        foreach (var id in ids)
            Assert.Equal("COU-" + numbers[id].ToString("D4", System.Globalization.CultureInfo.InvariantCulture), codes[id]);
        Assert.Equal(25, codes.Values.Distinct().Count());
        var remaining = ids.ToHashSet();
        var page = 1;
        while (remaining.Count > 0)
        {
            using var workload = await client.GetAsync($"/api/v1/couriers/workload?page={page++}&pageSize=100");
            workload.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await workload.Content.ReadAsStringAsync());
            var fleet = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.NotEmpty(fleet);
            foreach (var row in fleet)
            {
                var id = row.GetProperty("courierId").GetGuid();
                if (remaining.Remove(id)) Assert.Equal(codes[id], row.GetProperty("courierCode").GetString());
            }
        }
    }

    [Fact]
    public async Task FutureWorkDoesNotConsumeTodayAndEarlyPickupRemainsAllowed()
    {
        await using var a = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        await using var b = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        await using (var db = a.Context())
        {
            (await db.Couriers.SingleAsync(x => x.Id == a.CourierId)).Configure(true, true, 1);
            var zone = (await db.Parcels.SingleAsync(x => x.Id == b.ParcelId)).ZoneId;
            db.CourierZones.Add(new CourierZone(a.CourierId, zone));
            await db.SaveChangesAsync();
        }
        using var factory = a.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        using var future = await Assign(client, a.ParcelId, a.CourierId, date: "2026-10-29");
        Assert.Equal(HttpStatusCode.OK, future.StatusCode);
        using var todayOptions = await client.GetAsync(Options(b.ParcelId));
        Assert.Contains(a.CourierId.ToString(), await todayOptions.Content.ReadAsStringAsync());
        using var fullOptions = await client.GetAsync(Options(b.ParcelId, "?serviceDate=2026-10-29"));
        Assert.DoesNotContain(a.CourierId.ToString(), await fullOptions.Content.ReadAsStringAsync());
        using var earlyPickup = await a.UpdateAsync("PickedUp", 2);
        Assert.Equal(HttpStatusCode.OK, earlyPickup.StatusCode);
        using var failed = await a.UpdateAsync("Failed", 3, reason: "Other");
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        using var released = await client.GetAsync(Options(b.ParcelId, "?serviceDate=2026-10-29"));
        Assert.Contains(a.CourierId.ToString(), await released.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StorageFailureIsNotSuccessfulEmptyAvailability()
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), "Dispatcher"));
        using var client = factory.CreateClient();
        using var options = await client.GetAsync(Options(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, options.StatusCode);
        using var assignment = await Assign(client, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, assignment.StatusCode);
    }

    [Fact]
    public async Task ExpiredKeyCannotBeReusedAndDoesNotAddAssignmentOrEvent()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        using var response = await Assign(client, s.ParcelId, s.CourierId, key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var before = (await s.GraphAsync()).Signature();
        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(31);
        using var expired = await Assign(client, s.ParcelId, s.CourierId, key);
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
        Assert.Contains("key_expired", await expired.Content.ReadAsStringAsync());
        Assert.Equal(before, (await s.GraphAsync()).Signature());
    }
}
