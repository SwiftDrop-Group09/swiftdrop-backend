using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.Admin.Zones;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Admin;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Admin;

public sealed class ZoneSummaryHandlerTests
{
    [Theory]
    [InlineData(false, false, false, ZoneFailure.Unauthenticated)]
    [InlineData(true, false, false, ZoneFailure.Forbidden)]
    public async Task AuthorizationPrecedesDateValidation(bool authenticated, bool admin, bool operations, ZoneFailure failure)
    {
        var store = new SummaryStore();
        var handler = new ZoneAdministrationHandler(store, new SummaryUser(authenticated, admin, operations));
        var result = await handler.GetSummaryAsync("invalid", null, default);
        Assert.Equal(failure, result.Failure);
        Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NoBoundsPreservesAllDatesAndCancellation(bool admin, bool operations)
    {
        var store = new SummaryStore();
        var handler = new ZoneAdministrationHandler(store, new SummaryUser(true, admin, operations));
        using var cancellation = new CancellationTokenSource();
        var result = await handler.GetSummaryAsync(null, null, cancellation.Token);
        Assert.NotNull(result.Response);
        Assert.Empty(result.Response.Zones);
        Assert.Null(store.From);
        Assert.Null(store.To);
        Assert.Equal(cancellation.Token, store.Cancellation);
        Assert.Equal(1, store.Reads);
    }

    [Theory]
    [InlineData(null, "2026-11-15T00:00:00Z", "from")]
    [InlineData("2026-11-14T00:00:00Z", null, "to")]
    [InlineData("", "", "from")]
    [InlineData("invalid", "2026-11-15T00:00:00Z", "from")]
    [InlineData("2026-11-14", "2026-11-15T00:00:00Z", "from")]
    [InlineData("2026-11-14T00:00:00+05:30", "2026-11-15T00:00:00Z", "from")]
    [InlineData("2026-11-14T00:00:00", "2026-11-15T00:00:00Z", "from")]
    [InlineData("2026-11-14T00:00:00Z\n", "2026-11-15T00:00:00Z", "from")]
    [InlineData("2026-02-30T00:00:00Z", "2026-11-15T00:00:00Z", "from")]
    [InlineData("2026-11-14T00:00:00Z", "2026-11-14T00:00:00Z", "to")]
    [InlineData("2026-11-15T00:00:00Z", "2026-11-14T00:00:00Z", "to")]
    [InlineData("2026-01-01T00:00:00Z", "2027-01-02T00:00:00.0000001Z", "range")]
    public async Task InvalidRangeNeverReachesPersistence(string? from, string? to, string field)
    {
        var store = new SummaryStore();
        var handler = new ZoneAdministrationHandler(store, new SummaryUser(true, false, true));
        var result = await handler.GetSummaryAsync(from, to, default);
        Assert.Equal(ZoneFailure.InvalidDateRange, result.Failure);
        Assert.Contains(field, result.Errors!.Keys);
        Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData("2026-01-01T00:00:00Z", "2027-01-02T00:00:00Z")]
    [InlineData("2026-11-14T00:00:00.1234567+00:00", "2026-11-14T00:00:01+00:00")]
    public async Task ValidUtcBoundsReachPersistence(string from, string to)
    {
        var store = new SummaryStore();
        var handler = new ZoneAdministrationHandler(store, new SummaryUser(true, false, true));
        var result = await handler.GetSummaryAsync(from, to, default);
        Assert.NotNull(result.Response);
        Assert.Equal(DateTimeOffset.Parse(from), store.From);
        Assert.Equal(DateTimeOffset.Parse(to), store.To);
    }

    private sealed class SummaryUser(bool authenticated, bool admin, bool operations) : ICurrentDeliveryUser
    {
        public bool IsAuthenticated => authenticated;
        public Guid? UserId => authenticated ? Guid.NewGuid() : null;
        public bool IsAdmin => admin;
        public bool IsOperations => operations;
        public bool IsCourier => false;
        public bool IsCustomer => false;
    }

    private sealed class SummaryStore : IZoneAdministrationStore
    {
        public int Reads { get; private set; }
        public DateTimeOffset? From { get; private set; }
        public DateTimeOffset? To { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public Task<ZoneSummaryResponse> GetZoneSummaryAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
        {
            Reads++;
            From = from;
            To = to;
            Cancellation = cancellationToken;
            return Task.FromResult(new ZoneSummaryResponse([]));
        }
        public Task<ZonePage> ListZonesAsync(int page, int pageSize, CancellationToken cancellationToken, ZoneQuery? filters = null) => throw new NotSupportedException();
        public Task<ZoneDto?> GetZoneByIdAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ZoneResult<ZoneDto>> CreateZoneAsync(ZoneInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ZoneResult<ZoneDto>> UpdateZoneAsync(Guid id, ZoneInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

[Collection("Postgres")]
public sealed class ZoneSummaryApiTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset From = new(2037, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DeliveryDbContext Context() => new(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    [Theory]
    [InlineData("Dispatcher")]
    [InlineData("Admin")]
    public async Task TC_D05_CurrentStatusCountsMatchBookingBoundariesAndDeliveryZones(string role)
    {
        var zones = await SeedAsync();
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        var range = "?from=" + Uri.EscapeDataString(From.ToString("O")) + "&to=" + Uri.EscapeDataString(To.ToString("O"));
        var scoped = (await client.GetFromJsonAsync<ZoneSummaryResponse>("/api/v1/zones/summary" + range))!;
        AssertCounts(scoped, zones[0], Enum.GetNames<ParcelStatus>().ToDictionary(status => status, _ => 1));
        AssertCounts(scoped, zones[1], new() { ["Delivered"] = 2 });
        AssertCounts(scoped, zones[2], []);
        AssertCounts(scoped, zones[3], []);

        var all = (await client.GetFromJsonAsync<ZoneSummaryResponse>("/api/v1/zones/summary"))!;
        var first = Enum.GetNames<ParcelStatus>().ToDictionary(status => status, _ => 1);
        first["Created"] += 2;
        AssertCounts(all, zones[0], first);
        AssertCounts(all, zones[1], new() { ["Delivered"] = 2, ["Created"] = 1, ["Failed"] = 1 });
        AssertCounts(all, zones[2], []);
        AssertCounts(all, zones[3], []);

        // The production store and HTTP projection agree for the same persisted data.
        var persisted = await new PostgresZoneAdministrationStore(Context).GetZoneSummaryAsync(From, To, default);
        foreach (var zone in zones)
            AssertCounts(persisted, zone, scoped.Zones.Single(item => item.ZoneId == zone.Id).StatusCounts.ToDictionary());
    }

    [Theory]
    [InlineData("from=invalid", "invalid_date_range")]
    [InlineData("from=2026-11-14T00:00:00Z", "invalid_date_range")]
    [InlineData("to=2026-11-15T00:00:00Z", "invalid_date_range")]
    [InlineData("from=&to=", "invalid_date_range")]
    [InlineData("from=2026-11-14&to=2026-11-15", "invalid_date_range")]
    [InlineData("from=2026-11-14T00:00:00%2B05:30&to=2026-11-15T00:00:00Z", "invalid_date_range")]
    [InlineData("from=2026-11-14T00:00:00Z%0A&to=2026-11-15T00:00:00Z", "invalid_date_range")]
    [InlineData("from=2026-11-14T00:00:00Z&to=garbage", "invalid_date_range")]
    [InlineData("from=2026-11-14T00:00:00Z&to=2026-11-14T00:00:00Z", "invalid_date_range")]
    [InlineData("from=2026-11-15T00:00:00Z&to=2026-11-14T00:00:00Z", "invalid_date_range")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2027-01-03T00:00:00Z", "invalid_date_range")]
    [InlineData("from=x&from=y&to=z", "validation")]
    [InlineData("to=x&to=y", "validation")]
    [InlineData("unknown=value", "validation")]
    [InlineData("From=2026-11-14T00:00:00Z", "validation")]
    public async Task InvalidQueryReturnsProblemBeforeDatabaseAccess(string query, string code)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), "Dispatcher"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/zones/summary?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("correlationId", out _));
        if (code == "invalid_date_range") Assert.NotEmpty(problem.GetProperty("errors").EnumerateObject());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Customer", HttpStatusCode.Forbidden)]
    [InlineData("Courier", HttpStatusCode.Forbidden)]
    [InlineData("Merchant", HttpStatusCode.Forbidden)]
    public async Task TC_D05_ForbiddenRolesCannotReadSummaryEvenWithInvalidQuery(string? role, HttpStatusCode expected)
    {
        using var factory = new QuoteApiFactory(null, role is null ? null : QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        foreach (var query in new[] { "", "?from=invalid", "?unknown=1" })
        {
            using var response = await client.GetAsync("/api/v1/zones/summary" + query);
            Assert.Equal(expected, response.StatusCode);
        }
    }

    private static void AssertCounts(ZoneSummaryResponse response, DeliveryZone zone, Dictionary<string, int> expected)
    {
        var item = Assert.Single(response.Zones, item => item.ZoneId == zone.Id);
        Assert.Equal(zone.Name, item.Name);
        Assert.Equal(expected.OrderBy(pair => pair.Key), item.StatusCounts.OrderBy(pair => pair.Key));
        Assert.Equal(expected.Values.Sum(), item.Total);
    }

    private async Task<DeliveryZone[]> SeedAsync()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();
        var prefix = "SUMMARY-" + Guid.NewGuid().ToString("N");
        var zones = Enumerable.Range(0, 4).Select(index => new DeliveryZone(Guid.NewGuid(), prefix + index)).ToArray();
        zones[0].SetActive(false);
        db.AddRange(zones);
        var tiers = zones.Take(2).Select(zone => new PricingTier(Guid.NewGuid(), zone.Id, ParcelSize.Small, "summary-v1", 100m, 0m)).ToArray();
        db.AddRange(tiers);
        var statuses = new List<(Parcel Parcel, ParcelStatus Status)>();
        foreach (var status in Enum.GetValues<ParcelStatus>())
            Add(tiers[0], From, status, merchant: (int)status % 2 == 0);
        Add(tiers[0], From.AddTicks(-10), ParcelStatus.Created);
        Add(tiers[0], To, ParcelStatus.Created);
        Add(tiers[1], To.AddTicks(-10), ParcelStatus.Delivered);
        Add(tiers[1], From.AddHours(12), ParcelStatus.Delivered, merchant: true);
        Add(tiers[1], From.AddDays(-1), ParcelStatus.Created);
        Add(tiers[1], To, ParcelStatus.Failed);
        await db.SaveChangesAsync();
        foreach (var (parcel, status) in statuses)
        {
            db.Entry(parcel).Property(item => item.Status).CurrentValue = status;
            db.Entry(parcel).Property(item => item.Version).CurrentValue = 2;
            db.Entry(parcel).Property(item => item.UpdatedAt).CurrentValue = To.AddDays(2);
        }
        await db.SaveChangesAsync();
        return zones;

        void Add(PricingTier tier, DateTimeOffset createdAt, ParcelStatus status, bool merchant = false)
        {
            var id = Guid.NewGuid();
            var pickup = JsonSerializer.Serialize(new NormalizedAddress("Fixture pickup", "Colombo", "00100", zones[2].Id), Json);
            var dropoff = JsonSerializer.Serialize(new NormalizedAddress("Fixture dropoff", "Kandy", "20000", tier.ZoneId), Json);
            var recipient = JsonSerializer.Serialize(new NormalizedRecipient("Fixture recipient", "0000000000"), Json);
            var details = new ParcelDetails(ParcelSize.Small, 1m, tier.ZoneId, pickup, null, dropoff, recipient);
            var parcel = merchant
                ? Parcel.ForMerchant(id, Guid.NewGuid(), id.ToString(), prefix + id.ToString("N"), details, createdAt)
                : Parcel.ForCustomer(id, Guid.NewGuid(), prefix + id.ToString("N"), details, createdAt);
            db.AddRange(parcel, new PriceSnapshot(Guid.NewGuid(), id, tier, 100m, 0m, 100m, createdAt));
            statuses.Add((parcel, status));
        }
    }
}
