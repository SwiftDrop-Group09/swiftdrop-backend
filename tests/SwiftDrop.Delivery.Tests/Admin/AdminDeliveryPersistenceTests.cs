using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftDrop.Delivery.Api.Application.Admin.Pricing;
using SwiftDrop.Delivery.Api.Application.Admin.Zones;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Admin;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Admin;

[Collection("Postgres")]
public sealed class AdminDeliveryPersistenceTests(PostgresFixture fixture)
{
    private DeliveryDbContext Context() => new(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(fixture.ConnectionString).Options);
    private readonly Guid actor = Guid.NewGuid();

    [Fact]
    public async Task PricingOverviewPagesWholeZonesAndRetainsVersionsAndUnconfiguredSizes()
    {
        await using (var db = Context()) await db.Database.MigrateAsync();
        var zones = new PostgresZoneAdministrationStore(Context);
        var zone = (await zones.CreateZoneAsync(new("Tariff coverage " + Guid.NewGuid(), "Kandy", []), actor, DateTimeOffset.UtcNow, default)).Response!;
        var empty = (await zones.CreateZoneAsync(new("New coverage " + Guid.NewGuid(), "Matara", []), actor, DateTimeOffset.UtcNow, default)).Response!;
        var store = new PostgresPricingAdministrationStore(Context);
        await store.CreatePricingTierAsync(new(zone.Id, "Small", "v1", 350, 90), actor, DateTimeOffset.UtcNow, default);
        var small = (await store.CreatePricingTierAsync(new(zone.Id, "Small", "v2", 450, 60), actor, DateTimeOffset.UtcNow, default)).Response!;
        await store.CreatePricingTierAsync(new(zone.Id, "Medium", "v1", 600, 80), actor, DateTimeOffset.UtcNow, default);
        await store.CreatePricingTierAsync(new(zone.Id, "Large", "v1", 900, 100, IsActive: false), actor, DateTimeOffset.UtcNow, default);
        var page = await store.ListOverviewAsync(1, 1, zone.Id, default);
        Assert.Equal(1, page.TotalCount);
        var row = Assert.Single(page.Items);
        Assert.Equal(4, row.TierCount);
        Assert.Equal(2, row.ActiveTiers.Count);
        var activeSmall = Assert.Single(row.ActiveTiers, tier => tier.Size == "Small");
        Assert.Equal(small.Id, activeSmall.Id); Assert.Equal(small.Version, activeSmall.Version);
        Assert.Equal(450, activeSmall.BaseRate);
        Assert.DoesNotContain(row.ActiveTiers, tier => tier.Size == "Large" || !tier.IsActive);
        Assert.Empty((await store.ListOverviewAsync(2, 1, zone.Id, default)).Items);
        var unconfigured = Assert.Single((await store.ListOverviewAsync(1, 1, empty.Id, default)).Items);
        Assert.Equal(0, unconfigured.TierCount); Assert.Empty(unconfigured.ActiveTiers);
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Principal(actor.ToString(), "Admin"));
        using var client = factory.CreateClient();
        var response = await client.GetFromJsonAsync<PricingOverviewPage>($"/api/v1/pricing-tiers/overview?zoneId={zone.Id}&pageSize=1");
        Assert.Equal(4, Assert.Single(response!.Items).TierCount);
        var versions = await store.ListPricingAsync(1, 20, zone.Id, default);
        Assert.Equal(4, versions.TotalCount); Assert.Equal(2, versions.Items.Count(tier => !tier.IsActive));
    }

    [Theory]
    [InlineData("page=0")] [InlineData("pageSize=101")] [InlineData("page=invalid")]
    [InlineData("zoneId=invalid")] [InlineData("zoneId=00000000-0000-0000-0000-000000000000")]
    [InlineData("page=1&page=2")] [InlineData("status=active")]
    public async Task PricingOverviewRejectsInvalidQueries(string query)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(actor.ToString(), "Admin"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/pricing-tiers/overview?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ZoneFiltersRunBeforePaginationAndReturnGlobalCityOptions()
    {
        var marker = Guid.NewGuid().ToString("N"); var postal = "00"+Guid.NewGuid().ToString("N");
        Guid last;
        await using (var db=Context())
        {
            await db.Database.MigrateAsync();
            for(var i=0;i<20;i++) db.Zones.Add(new DeliveryZone(Guid.NewGuid(),marker+i.ToString("D2"),boundaries:ZoneBoundaryHelper.SerializeBoundaries("Colombo",[])));
            var target=new DeliveryZone(Guid.NewGuid(),marker+"ZZ",boundaries:ZoneBoundaryHelper.SerializeBoundaries("Galle",[postal]));
            target.SetActive(false);last=target.Id;db.Zones.Add(target);await db.SaveChangesAsync();
        }
        var store=new PostgresZoneAdministrationStore(Context);
        var first=await store.ListZonesAsync(1,20,default,new(Search:marker));
        Assert.Equal(21,first.TotalCount);Assert.Equal(20,first.Items.Count);Assert.DoesNotContain(first.Items,item=>item.Id==last);
        var city=await store.ListZonesAsync(1,20,default,new(Search:marker,City:"gALLE",Status:"inactive"));
        Assert.Equal(1,city.TotalCount);Assert.Equal(last,Assert.Single(city.Items).Id);Assert.True(city.Items[0].Version>0);
        Assert.Contains("Colombo",city.Cities!);Assert.Contains("Galle",city.Cities!);
        var excluded=await store.ListZonesAsync(1,1,default,new(Search:marker,NotCity:"Galle"));
        Assert.Equal(20,excluded.TotalCount);Assert.Single(excluded.Items);
        var code=await store.ListZonesAsync(1,20,default,new(Search:postal));
        Assert.Equal(last,Assert.Single(code.Items).Id);
        var literal=await store.ListZonesAsync(1,20,default,new(Search:marker+"%_' OR 1=1 --"));
        Assert.Empty(literal.Items);Assert.Equal(0,literal.TotalCount);Assert.Contains("Galle",literal.Cities!);
    }

    [Theory]
    [InlineData("page=0")] [InlineData("pageSize=101")] [InlineData("page=a")]
    [InlineData("city=Galle&city=Kandy")] [InlineData("status=unknown")]
    [InlineData("unexpected=1")]
    public async Task ZoneHttpReadsRejectInvalidFilters(string query)
    {
        await using(var db=Context()) await db.Database.MigrateAsync();
        using var factory=new QuoteApiFactory(fixture.ConnectionString,QuoteApiFactory.Principal(actor.ToString(),"Admin"));
        using var client=factory.CreateClient();
        using var response=await client.GetAsync("/api/v1/zones?"+query);
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
    }

    [Fact]
    public async Task ZoneChangesUsePersistedVersionsAndRejectLostUpdatesWithoutExtraAudit()
    {
        await using (var db = Context()) await db.Database.MigrateAsync();
        var store = new PostgresZoneAdministrationStore(Context);
        var created = (await store.CreateZoneAsync(new("TESTDATA zone " + Guid.NewGuid(), "Kandy", []), actor, DateTimeOffset.UtcNow, default)).Response!;
        Assert.True(created.Version > 0);
        var changed = await store.UpdateZoneAsync(created.Id, new(created.Name, "Matale", [], true, created.Version), actor, DateTimeOffset.UtcNow, default);
        Assert.NotNull(changed.Response);
        Assert.NotEqual(created.Version, changed.Response.Version);
        var stale = await store.UpdateZoneAsync(created.Id, new(created.Name, "Galle", [], true, created.Version), actor, DateTimeOffset.UtcNow, default);
        Assert.Equal(ZoneFailure.Conflict, stale.Failure);
        await using var verify = Context();
        Assert.Equal(2, await verify.ConfigAuditLogs.CountAsync(row => row.ResourceId == created.Id));
        Assert.Equal("Matale", (await store.GetZoneByIdAsync(created.Id, default))!.City);
    }

    [Fact]
    public async Task ActivatingNewTariffRetainsOldEconomicsAndOnlyOneActiveVersion()
    {
        await using (var db = Context()) await db.Database.MigrateAsync();
        var zones = new PostgresZoneAdministrationStore(Context);
        var zone = (await zones.CreateZoneAsync(new("TESTDATA tariff " + Guid.NewGuid(), "Colombo", []), actor, DateTimeOffset.UtcNow, default)).Response!;
        var store = new PostgresPricingAdministrationStore(Context);
        var first = (await store.CreatePricingTierAsync(new(zone.Id, "Small", "v1", 350, 90), actor, DateTimeOffset.UtcNow, default)).Response!;
        var second = (await store.CreatePricingTierAsync(new(zone.Id, "Small", "v2", 400, 100), actor, DateTimeOffset.UtcNow, default)).Response!;
        await using (var db = Context())
        {
            var old = await db.PricingTiers.SingleAsync(tier => tier.Id == first.Id);
            Assert.False(old.IsActive);
            Assert.Equal(350, old.BaseRate);
            Assert.Equal(90, old.PerKgRate);
            Assert.Equal(1, await db.PricingTiers.CountAsync(tier => tier.ZoneId == zone.Id && tier.Size == ParcelSize.Small && tier.IsActive));
        }
        var stale = await store.UpdatePricingTierAsync(first.Id, new(true, first.Version), actor, DateTimeOffset.UtcNow, default);
        Assert.Equal(PricingFailure.Conflict, stale.Failure);
        var disabled = await store.UpdatePricingTierAsync(second.Id, new(false, second.Version), actor, DateTimeOffset.UtcNow, default);
        Assert.False(disabled.Response!.IsActive);
        Assert.NotEqual(second.Version, disabled.Response.Version);
    }

    [Fact]
    public async Task ConcurrentOverlappingPostalAssignmentsProduceOnlyOneWinner()
    {
        await using (var db = Context()) await db.Database.MigrateAsync();
        var code = "C" + Guid.NewGuid().ToString("N");
        var store = new PostgresZoneAdministrationStore(Context);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => store.CreateZoneAsync(
            new("TESTDATA overlap " + Guid.NewGuid(), "Galle", [code]), actor, DateTimeOffset.UtcNow, default)));
        Assert.Single(results, result => result.Response is not null);
        Assert.Single(results, result => result.Failure == ZoneFailure.OverlappingPostalCodes);
    }

    [Fact]
    public async Task DatabaseRejectsConfigurationAuditRewrites()
    {
        await using (var db = Context()) await db.Database.MigrateAsync();
        var store = new PostgresZoneAdministrationStore(Context);
        var zone = (await store.CreateZoneAsync(new("TESTDATA audit " + Guid.NewGuid(), "Gampaha", []), actor, DateTimeOffset.UtcNow, default)).Response!;
        await using var verify = Context();
        var failure = await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM delivery_config_audits WHERE resource_id = {zone.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(1, await verify.ConfigAuditLogs.CountAsync(row => row.ResourceId == zone.Id));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Customer", HttpStatusCode.Forbidden)]
    [InlineData("Courier", HttpStatusCode.Forbidden)]
    [InlineData("Dispatcher", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.ServiceUnavailable)]
    public async Task HttpAuthorizationPrecedesUnavailableDatabase(string? role, HttpStatusCode expected)
    {
        using var factory = new QuoteApiFactory(null, role is null ? null : QuoteApiFactory.Principal(actor.ToString(), role));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/zones");
        Assert.Equal(expected, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var overview = await client.GetAsync("/api/v1/pricing-tiers/overview");
        Assert.Equal(expected, overview.StatusCode);
        Assert.True(overview.Headers.CacheControl?.NoStore);
    }
}
