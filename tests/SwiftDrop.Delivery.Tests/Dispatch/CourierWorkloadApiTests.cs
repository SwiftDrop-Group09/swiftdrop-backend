using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.Dispatch;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Courier;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Dispatch;

[Collection("Postgres")]
public sealed class CourierWorkloadApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Workload = "/api/v1/couriers/workload";
    private static readonly DateTimeOffset Now = QuoteApiFactory.Now;
    private DeliveryDbContext Context() => new(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(fixture.ConnectionString).Options);
    public async Task InitializeAsync() { await using var db = Context(); await db.Database.MigrateAsync(); }
    public Task DisposeAsync() => Task.CompletedTask;
    private QuoteApiFactory Factory(string role = "Dispatcher") => new(fixture.ConnectionString, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
    private static string Assignments(Guid id) => $"/api/v1/couriers/{id}/assignments";

    [Theory]
    [InlineData("Dispatcher")]
    [InlineData("Admin")]
    public async Task FleetIncludesAllProfilesAndAllDatesWithoutMultiplyingZoneCounts(string role)
    {
        var seed = await SeedAsync();
        using var factory = Factory(role);
        using var client = factory.CreateClient();
        var all = new List<CourierWorkloadResponse>();
        WorkloadPage result;
        var page = 0;
        do
        {
            result = await Read<WorkloadPage>(client, Workload + $"?page={++page}&pageSize=100");
            all.AddRange(result.Items);
        } while (all.Count < result.TotalCount);
        await using var db = Context();
        Assert.Equal(await db.Couriers.CountAsync(), result.TotalCount);
        Assert.Equal(all.Select(row => row.CourierId).Order(), all.Select(row => row.CourierId));
        Assert.Equal(all.Count, all.Select(row => row.CourierId).Distinct().Count());
        var busy = Assert.Single(all, row => row.CourierId == seed.Busy);
        Assert.True(busy.Active);
        Assert.True(busy.OnDuty);
        Assert.Equal(3, busy.ActiveCount);
        Assert.Equal(2, busy.Capacity); // Persisted over-capacity work must still be visible.
        Assert.Equal(seed.Zones.Order(), busy.ZoneIds);
        var inactive = Assert.Single(all, row => row.CourierId == seed.Inactive);
        Assert.False(inactive.Active);
        Assert.False(inactive.OnDuty);
        Assert.Equal(1, inactive.ActiveCount);
        var zero = Assert.Single(all, row => row.CourierId == seed.Zero);
        Assert.True(zero.Active);
        Assert.False(zero.OnDuty);
        Assert.Equal(0, zero.ActiveCount);
        Assert.Empty(zero.ZoneIds);
        var empty = await Read<DispatchAssignmentsPage>(client, Assignments(seed.Zero));
        Assert.Equal(0, empty.TotalCount);
        Assert.Empty(empty.Items);
    }

    [Fact]
    public async Task AssignmentPagesHaveStableTiesAllDatesAndMinimalParcelProjection()
    {
        var seed = await SeedAsync();
        using var factory = Factory();
        using var client = factory.CreateClient();
        var first = await Read<DispatchAssignmentsPage>(client, Assignments(seed.Busy) + "?pageSize=2");
        var second = await Read<DispatchAssignmentsPage>(client, Assignments(seed.Busy) + "?pageSize=2&page=2");
        Assert.Equal(seed.Busy, first.CourierId);
        Assert.Equal(3, first.TotalCount);
        Assert.Equal(3, second.TotalCount);
        Assert.Equal(seed.Assignments.OrderByDescending(row => row.AssignmentId).Select(row => row.ParcelId),
            first.Items.Concat(second.Items).Select(row => row.ParcelId));
        Assert.Equal(3, first.Items.Concat(second.Items).Select(row => row.ServiceDate).Distinct().Count());
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(await Read<DispatchAssignmentsPage>(client, Assignments(seed.Busy) + "?pageSize=2")));
        using var response = await client.GetAsync(Assignments(seed.Busy));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = json.RootElement.GetProperty("items")[0];
        Assert.Equal(new[] { "assignedAt", "parcelId", "serviceDate", "status", "trackingNumber" }, item.EnumerateObject().Select(field => field.Name).Order());
        var beyond = await Read<DispatchAssignmentsPage>(client, Assignments(seed.Busy) + "?page=9");
        Assert.Equal(3, beyond.TotalCount);
        Assert.Empty(beyond.Items);
    }

    [Fact]
    public async Task FleetPaginationOrdersCouriersIndependentlyOfTheirAssignments()
    {
        await SeedAsync();
        using var factory = Factory();
        using var client = factory.CreateClient();
        var first = await Read<WorkloadPage>(client, Workload + "?pageSize=2");
        var second = await Read<WorkloadPage>(client, Workload + "?page=2&pageSize=2");
        await using var db = Context();
        var expected = await db.Couriers.OrderBy(courier => courier.Id).Take(4).Select(courier => courier.Id).ToArrayAsync();
        Assert.Equal(expected, first.Items.Concat(second.Items).Select(row => row.CourierId));
        Assert.Equal(first.TotalCount, second.TotalCount);
        using var response = await client.GetAsync(Workload);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=1&page=2")]
    [InlineData("pageSize=20&pageSize=10")]
    [InlineData("date=2026-10-23")]
    [InlineData("courierId=foreign")]
    public async Task BothOperationsRejectInvalidOrUndocumentedParameters(string query)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        foreach (var path in new[] { Workload, Assignments(Guid.NewGuid()) })
        {
            using var response = await client.GetAsync(path + "?" + query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Customer", HttpStatusCode.Forbidden)]
    [InlineData("Courier", HttpStatusCode.Forbidden)]
    [InlineData("Merchant", HttpStatusCode.Forbidden)]
    public async Task WrongRolesCannotEnumerateFleetAndAuthPrecedesValidation(string? role, HttpStatusCode status)
    {
        using var factory = new QuoteApiFactory(null, role is null ? null : QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        foreach (var path in new[] { Workload, "/api/v1/couriers/invalid/assignments" })
        {
            using var response = await client.GetAsync(path + "?page=bad");
            Assert.Equal(status, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task MissingOrMalformedCourierReturnsCourierSpecificNotFound()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        foreach (var id in new[] { Guid.NewGuid().ToString(), "invalid", Guid.Empty.ToString() })
        {
            using var response = await client.GetAsync($"/api/v1/couriers/{id}/assignments");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("courier", body);
            Assert.DoesNotContain("parcel was not found", body);
        }
    }

    [Fact]
    public async Task MissingStorageReturns503RatherThanSuccessfulZeroCounts()
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), "Dispatcher"));
        using var client = factory.CreateClient();
        foreach (var path in new[] { Workload, Assignments(Guid.NewGuid()) })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }

    [Fact]
    public async Task RealAuthenticationRejectsAnUnverifiedBearerBeforeStorage()
    {
        using var factory = new QuoteApiFactory(null, replaceAuthentication: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "unverified-dispatcher-token");
        foreach (var path in new[] { Workload, Assignments(Guid.NewGuid()) })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingCourierFailureOrProofCompletionRemovesOutstandingWork(bool deliver)
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture,
            deliver ? ParcelStatus.OutForDelivery : ParcelStatus.Assigned,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        using var factory = Factory();
        using var client = factory.CreateClient();
        Assert.Equal(1, (await Read<DispatchAssignmentsPage>(client, Assignments(scenario.CourierId))).TotalCount);
        using var mutation = deliver
            ? await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form())
            : await scenario.UpdateAsync("Failed", 2, reason: "RecipientUnavailable");
        Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
        Assert.Empty((await Read<DispatchAssignmentsPage>(client, Assignments(scenario.CourierId))).Items);
        var store = new Api.Infrastructure.Dispatch.PostgresWorkloadReadStore(Context);
        var all = await store.GetWorkloadAsync(1, int.MaxValue, CancellationToken.None);
        Assert.Equal(0, Assert.Single(all.Items, row => row.CourierId == scenario.CourierId).ActiveCount);
        // The self-manifest still belongs only to the Courier role.
        using var denied = await client.GetAsync("/api/v1/couriers/me/assignments");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private static async Task<T> Read<T>(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Seed> SeedAsync()
    {
        await using var db = Context();
        var busy = Guid.NewGuid();
        var inactive = Guid.NewGuid();
        var zero = Guid.NewGuid();
        var zones = new[] { new DeliveryZone(Guid.NewGuid(), "Dispatch A " + Guid.NewGuid().ToString("N")), new DeliveryZone(Guid.NewGuid(), "Dispatch B " + Guid.NewGuid().ToString("N")) };
        db.AddRange(zones);
        var tier = new PricingTier(Guid.NewGuid(), zones[0].Id, ParcelSize.Small, "dispatch-fixture-v1", 100m, 0m);
        db.Add(tier);
        db.AddRange(new Api.Domain.Courier(busy, true, true, 2), new Api.Domain.Courier(inactive, false, false, 10),
            new Api.Domain.Courier(zero, true, false, 10));
        db.AddRange(new CourierZone(busy, zones[0].Id), new CourierZone(busy, zones[1].Id));
        var active = new List<(Guid AssignmentId, Guid ParcelId)>();
        for (var offset = -1; offset <= 1; offset++) active.Add(await AddAssignment(busy, AssignmentStatus.Active, offset));
        await AddAssignment(inactive, AssignmentStatus.Active, 0);
        foreach (var status in new[] { AssignmentStatus.Delivered, AssignmentStatus.Failed, AssignmentStatus.Cancelled, AssignmentStatus.Reassigned })
            await AddAssignment(busy, status, 0);
        await db.SaveChangesAsync();
        return new(busy, inactive, zero, zones.Select(zone => zone.Id).ToArray(), active.ToArray());

        async Task<(Guid, Guid)> AddAssignment(Guid courier, AssignmentStatus status, int offset)
        {
            var details = new ParcelDetails(ParcelSize.Small, 1m, zones[0].Id, "private pickup", null, "private dropoff", "private recipient");
            var parcel = Parcel.ForCustomer(Guid.NewGuid(), Guid.NewGuid(), "DISP-" + Guid.NewGuid().ToString("N"), details, Now.AddHours(-1));
            var assignment = new CourierAssignment(Guid.NewGuid(), parcel.Id, courier, DateOnly.FromDateTime(Now.UtcDateTime).AddDays(offset), Now);
            db.AddRange(parcel, new PriceSnapshot(Guid.NewGuid(), parcel.Id, tier, 100m, 0m, 100m, Now.AddHours(-1)));
            await db.SaveChangesAsync();
            // Retain each legal assignment state; fixtures obey the existing initial-state guards.
            db.Add(assignment);
            db.Entry(parcel).Property(row => row.Status).CurrentValue = ParcelStatus.Assigned;
            db.Entry(parcel).Property(row => row.Version).CurrentValue = 2;
            await db.SaveChangesAsync();
            switch (status)
            {
                case AssignmentStatus.Delivered:
                    assignment.MarkPickedUp(Now.AddMinutes(1));
                    await db.SaveChangesAsync();
                    assignment.MarkDelivered(Now.AddMinutes(2));
                    break;
                case AssignmentStatus.Failed: assignment.MarkFailed(); break;
                case AssignmentStatus.Cancelled: assignment.MarkCancelled(); break;
                case AssignmentStatus.Reassigned: assignment.MarkReassigned(); break;
            }
            await db.SaveChangesAsync();
            return (assignment.Id, parcel.Id);
        }
    }

    private sealed record Seed(Guid Busy, Guid Inactive, Guid Zero, Guid[] Zones, (Guid AssignmentId, Guid ParcelId)[] Assignments);
}
