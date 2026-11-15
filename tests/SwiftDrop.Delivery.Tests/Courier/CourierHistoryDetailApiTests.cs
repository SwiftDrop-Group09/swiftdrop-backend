using System.Net;
using System.Text.Json;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Authentication;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierHistoryDetailApiTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CompletedOwnerGetsEssentialFactsWithoutReopeningPrivateDeliveryData()
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        using var completed = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form());
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        using var response = await scenario.Client.GetAsync(Path(scenario.ParcelId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = json.RootElement;
        Assert.Equal("Delivered", row.GetProperty("status").GetString());
        Assert.Equal("2026-10-28", row.GetProperty("serviceDate").GetString());
        Assert.True(row.GetProperty("hasProof").GetBoolean());
        Assert.NotNull(row.GetProperty("pickupCity").GetString());
        Assert.NotNull(row.GetProperty("pickedUpAt").GetString());
        Assert.NotNull(row.GetProperty("completedAt").GetString());
        foreach (var field in new[] { "recipient", "phoneNumber", "pickupAddress", "deliveryAddress", "storageRef", "deliveryNote", "courierId", "customerId" })
            Assert.False(row.TryGetProperty(field, out _), field);
        using var privateDetail = await scenario.Client.GetAsync($"/api/v1/parcels/{scenario.ParcelId}");
        Assert.Equal(HttpStatusCode.NotFound, privateDetail.StatusCode);
    }

    [Fact]
    public async Task FailedOwnerGetsCanonicalReasonButNotInternalNotesAndFormerCourierGets404()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, merchant: true);
        Assert.True(await scenario.ReassignAsync(2));
        using var replacementFactory = scenario.CreateFactory(scenario.OtherCourierId);
        using var replacement = replacementFactory.CreateClient();
        using var failure = await scenario.UpdateAsync("Failed", 3, notes: "Private operational note", reason: "Damaged", client: replacement);
        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        using var oldRead = await scenario.Client.GetAsync(Path(scenario.ParcelId));
        Assert.Equal(HttpStatusCode.NotFound, oldRead.StatusCode);
        using var read = await replacement.GetAsync(Path(scenario.ParcelId));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var body = await read.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Damaged", json.RootElement.GetProperty("failureReason").GetString());
        Assert.Equal("Failed", json.RootElement.GetProperty("status").GetString());
        Assert.False(json.RootElement.GetProperty("hasProof").GetBoolean());
        Assert.DoesNotContain("Private operational note", body);
    }

    [Fact]
    public async Task ActiveForeignAndUnknownParcelsAreIndistinguishableFromMissingHistory()
    {
        await using var active = await StatusTestScenario.CreateAsync(fixture);
        await using var foreign = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Failed);
        foreach (var id in new[] { active.ParcelId, foreign.ParcelId, Guid.NewGuid() })
        {
            using var response = await active.Client.GetAsync(Path(id));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Merchant")]
    [InlineData("Dispatcher")]
    [InlineData("Admin")]
    public async Task OtherRolesCannotReadCourierHistoryDetails(string role)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Path(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task OptionalStageNotesStillPersistHistoryAndReplaySafely(string? notes)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var version = 2;
        foreach (var status in new[] { "PickedUp", "InTransit", "OutForDelivery" })
        {
            var key = Guid.NewGuid().ToString();
            using var response = await scenario.UpdateAsync(status, version, key, notes: notes);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var replay = await scenario.UpdateAsync(status, version, key, notes: null);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(await response.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
            version++;
        }
        var graph = await scenario.GraphAsync();
        Assert.Equal(ParcelStatus.OutForDelivery, graph.Parcel.Status);
        Assert.All(graph.History.Where(row => row.ToStatus is ParcelStatus.PickedUp or ParcelStatus.InTransit or ParcelStatus.OutForDelivery), row => Assert.Null(row.DeliveryNote));
    }

    private static string Path(Guid id) => $"/api/v1/couriers/me/history/{id:D}";
}
