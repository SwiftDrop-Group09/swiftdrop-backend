using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Quotes;

[Collection("Postgres")]
public sealed class BookingZoneCatalogTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CustomerReceivesOnlyActiveZoneIdsAndNamesWithoutPricingOrBoundaries()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();
        var active = new DeliveryZone(Guid.NewGuid(), "TESTDATA Bookable area", "private configuration", "{}");
        var inactive = new DeliveryZone(Guid.NewGuid(), "TESTDATA Inactive area");
        db.Zones.AddRange(active, inactive);
        db.Entry(inactive).Property(x => x.IsActive).CurrentValue = false;
        await db.SaveChangesAsync();
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Customer(Guid.NewGuid()));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/quotes/zones");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = json.RootElement.EnumerateArray().ToArray();
        Assert.Contains(rows, row => row.GetProperty("id").GetGuid() == active.Id);
        Assert.DoesNotContain(rows, row => row.GetProperty("id").GetGuid() == inactive.Id);
        Assert.All(rows, row => Assert.Equal(new[] { "id", "name" }, row.EnumerateObject().Select(x => x.Name).Order().ToArray()));
        Assert.DoesNotContain("private configuration", json.RootElement.GetRawText());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Courier", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.Forbidden)]
    [InlineData("Merchant", HttpStatusCode.Forbidden)]
    [InlineData("Customer", HttpStatusCode.ServiceUnavailable)]
    public async Task PermissionChecksPrecedeUnavailableStorage(string? role, HttpStatusCode expected)
    {
        using var factory = new QuoteApiFactory(null, role is null ? null : QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/quotes/zones");
        Assert.Equal(expected, response.StatusCode);
    }

    private DeliveryDbContext Context() => new(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(fixture.ConnectionString).Options);
}
