using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.DeliveryReads;

[Collection("Postgres")]
public sealed class ParcelZoneFilterApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = QuoteApiFactory.Now;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DeliveryDbContext Context() => new(new DbContextOptionsBuilder<DeliveryDbContext>()
        .UseNpgsql(fixture.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("Dispatcher")]
    [InlineData("Admin")]
    public async Task OperationsFiltersDeliveryZoneBeforeCountingAndPaging(string role)
    {
        var seed = await SeedAsync();
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        var query = $"status=Created&search={seed.Prefix}&zoneId={seed.DeliveryZone}&from={Now.UtcDateTime:O}&to={Now.AddDays(1).UtcDateTime:O}&pageSize=2";
        var first = await ReadAsync(client, query + "&page=1");
        var second = await ReadAsync(client, query + "&page=2");

        Assert.Equal(4, first.TotalCount);
        Assert.Equal(4, second.TotalCount);
        Assert.Equal(seed.CurrentDeliveryParcels.OrderDescending(), first.Items.Concat(second.Items).Select(item => item.Id));
        Assert.All(first.Items.Concat(second.Items), item => Assert.Equal("Created", item.Status));
        Assert.Equal(first.Items.Select(item => item.Id), (await ReadAsync(client, query + "&page=1")).Items.Select(item => item.Id));

        var pickupZone = await ReadAsync(client, $"status=Created&search={seed.Prefix}&zoneId={seed.PickupZone}");
        Assert.Equal(seed.PickupZoneParcel, Assert.Single(pickupZone.Items).Id);
        var allZones = await ReadAsync(client, $"status=Created&search={seed.Prefix}");
        Assert.Equal(6, allZones.TotalCount);
        var unmatched = await ReadAsync(client, $"status=Created&search={seed.Prefix}&zoneId={Guid.NewGuid()}");
        Assert.Empty(unmatched.Items);
        Assert.Equal(0, unmatched.TotalCount);
    }

    [Fact]
    public async Task CustomerZoneFilterCannotExposeAnotherOwnersParcelsOrCounts()
    {
        var seed = await SeedAsync();
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Customer(seed.Owner));
        using var client = factory.CreateClient();
        var result = await ReadAsync(client, $"status=Created&search={seed.Prefix}&zoneId={seed.DeliveryZone}&from={Now.UtcDateTime:O}");

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(seed.OwnCurrentDeliveryParcels.OrderDescending(), result.Items.Select(item => item.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("%20")]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111-1111-4111-8111-111111111111&zoneId=22222222-2222-4222-8222-222222222222")]
    public async Task InvalidZoneQueryReturnsBadRequest(string value)
    {
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), "Dispatcher"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/parcels?status=Created&zoneId=" + value);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Courier", HttpStatusCode.Forbidden)]
    [InlineData("Merchant", HttpStatusCode.Forbidden)]
    public async Task AuthorizationPrecedesZoneValidation(string? role, HttpStatusCode expected)
    {
        using var factory = new QuoteApiFactory(fixture.ConnectionString,
            role is null ? null : QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/parcels?zoneId=invalid");

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ZoneOptionsIncludeInactiveAndEmptyZonesForOperations()
    {
        var seed = await SeedAsync();
        foreach (var role in new[] { "Dispatcher", "Admin" })
        {
            using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/zones/summary");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var zones = body.RootElement.GetProperty("zones").EnumerateArray().ToArray();
            Assert.Contains(zones, zone => zone.GetProperty("zoneId").GetGuid() == seed.DeliveryZone);
            var empty = Assert.Single(zones, zone => zone.GetProperty("zoneId").GetGuid() == seed.EmptyZone);
            Assert.Equal(0, empty.GetProperty("total").GetInt32());
        }
    }

    private static async Task<CustomerParcelPage> ReadAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync("/api/v1/parcels?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CustomerParcelPage>())!;
    }

    private async Task<Scenario> SeedAsync()
    {
        await using var db = Context();
        var prefix = "ZONE-" + Guid.NewGuid().ToString("N");
        var pickup = new DeliveryZone(Guid.NewGuid(), prefix + " pickup");
        var delivery = new DeliveryZone(Guid.NewGuid(), prefix + " delivery");
        delivery.SetActive(false);
        var empty = new DeliveryZone(Guid.NewGuid(), prefix + " empty");
        var pickupTier = new PricingTier(Guid.NewGuid(), pickup.Id, ParcelSize.Small, "zone-v1", 100m, 0m);
        var deliveryTier = new PricingTier(Guid.NewGuid(), delivery.Id, ParcelSize.Small, "zone-v1", 100m, 0m);
        db.AddRange(pickup, delivery, empty, pickupTier, deliveryTier);
        var owner = Guid.NewGuid();
        var own = new[]
        {
            AddParcel(db, prefix, owner, pickup.Id, deliveryTier, Now),
            AddParcel(db, prefix, owner, pickup.Id, deliveryTier, Now)
        };
        var foreign = AddParcel(db, prefix, Guid.NewGuid(), pickup.Id, deliveryTier, Now);
        var merchant = AddParcel(db, prefix, Guid.NewGuid(), pickup.Id, deliveryTier, Now, merchant: true);
        var assignedId = AddParcel(db, prefix, owner, pickup.Id, deliveryTier, Now);
        AddParcel(db, prefix, owner, pickup.Id, deliveryTier, Now.AddDays(-1));
        var pickupParcel = AddParcel(db, prefix, owner, pickup.Id, pickupTier, Now);
        await db.SaveChangesAsync();
        // Persist a legal pre-existing assignment snapshot for the read fixture.
        var assigned = db.Parcels.Local.Single(parcel => parcel.Id == assignedId);
        db.Entry(assigned).Property(parcel => parcel.Status).CurrentValue = ParcelStatus.Assigned;
        db.Entry(assigned).Property(parcel => parcel.Version).CurrentValue = 2;
        db.Entry(assigned).Property(parcel => parcel.UpdatedAt).CurrentValue = Now.AddMinutes(1);
        await db.SaveChangesAsync();
        return new(prefix, owner, pickup.Id, delivery.Id, empty.Id, pickupParcel, own, [.. own, foreign, merchant]);
    }

    private static Guid AddParcel(DeliveryDbContext db, string prefix, Guid owner, Guid pickupZone,
        PricingTier tier, DateTimeOffset createdAt, bool merchant = false)
    {
        var pickup = JsonSerializer.Serialize(new NormalizedAddress("Test pickup", "Colombo", "00100", pickupZone), Json);
        var delivery = JsonSerializer.Serialize(new NormalizedAddress("Test delivery", "Kandy", "20000", tier.ZoneId), Json);
        var recipient = JsonSerializer.Serialize(new NormalizedRecipient("Test Recipient", "0000000000"), Json);
        var details = new ParcelDetails(ParcelSize.Small, 1m, tier.ZoneId, pickup, null, delivery, recipient);
        var id = Guid.NewGuid();
        var parcel = merchant
            ? Parcel.ForMerchant(id, owner, id.ToString(), prefix + id.ToString("N"), details, createdAt)
            : Parcel.ForCustomer(id, owner, prefix + id.ToString("N"), details, createdAt);
        db.AddRange(parcel, new PriceSnapshot(Guid.NewGuid(), id, tier, 100m, 0m, 100m, createdAt));
        return id;
    }

    private sealed record Scenario(string Prefix, Guid Owner, Guid PickupZone, Guid DeliveryZone,
        Guid EmptyZone, Guid PickupZoneParcel, Guid[] OwnCurrentDeliveryParcels, Guid[] CurrentDeliveryParcels);
}
