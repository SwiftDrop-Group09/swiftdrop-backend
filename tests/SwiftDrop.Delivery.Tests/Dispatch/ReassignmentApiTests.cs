using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.Dispatch;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Courier;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Dispatch;

[Collection("Postgres")]
public sealed class ReassignmentApiTests(PostgresFixture fixture)
{
    internal static async Task<HttpResponseMessage> Send(HttpClient client, Guid parcel, Guid courier,
        long version = 2, string? key = null, string? reason = "Replacement needed", string? date = null, bool reassign = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deliveries/assign")
        { Content = JsonContent.Create(new { parcelId = parcel, courierId = courier, expectedVersion = version, reassign, serviceDate = date, reason }) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("Dispatcher")]
    [InlineData("Admin")]
    public async Task ReplacementCommitsRetainedHistoryAndNewOwnership(string role)
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        var before = await s.GraphAsync();
        using var factory = s.CreateFactory(Guid.NewGuid(), role);
        using var client = factory.CreateClient();
        using var response = await Send(client, s.ParcelId, s.OtherCourierId, reason: "  Replacement needed  ");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var graph = await s.GraphAsync();
        Assert.Equal(3, graph.Parcel.Version);
        Assert.Equal(ParcelStatus.Assigned, graph.Parcel.Status);
        Assert.Equal(2, graph.Assignments.Length);
        var old = Assert.Single(graph.Assignments, a => a.Status == AssignmentStatus.Reassigned);
        Assert.Equal(before.Assignments[0].Id, old.Id);
        Assert.Equal(before.Assignments[0].ServiceDate, old.ServiceDate);
        Assert.Equal(before.Assignments[0].AssignedAt, old.AssignedAt);
        Assert.Equal(s.OtherCourierId, Assert.Single(graph.Assignments, a => a.Status == AssignmentStatus.Active).CourierId);
        Assert.Equal("Replacement needed", graph.History.Last().ChangeReason);
        Assert.Equal(before.Events.Length + 1, graph.Events.Length);
        using var denied = await s.Client.GetAsync($"/api/v1/parcels/{s.ParcelId}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var deniedWrite = await s.UpdateAsync("PickedUp", 3);
        Assert.Equal(HttpStatusCode.NotFound, deniedWrite.StatusCode);
        using var replacementFactory = s.CreateFactory(s.OtherCourierId);
        using var replacementClient = replacementFactory.CreateClient();
        using var visible = await replacementClient.GetAsync($"/api/v1/parcels/{s.ParcelId}");
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
        using var oldManifest = await s.Client.GetAsync("/api/v1/couriers/me/assignments");
        Assert.DoesNotContain(s.ParcelId.ToString(), await oldManifest.Content.ReadAsStringAsync());
        using var newManifest = await replacementClient.GetAsync("/api/v1/couriers/me/assignments");
        Assert.Contains(s.ParcelId.ToString(), await newManifest.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SameCourierStaleVersionAndPostPickupAreRejectedWithoutChanges()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var before = (await s.GraphAsync()).Signature();
        using var same = await Send(client, s.ParcelId, s.CourierId);
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        using var body = JsonDocument.Parse(await same.Content.ReadAsStringAsync());
        Assert.Equal("validation", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("Choose a different courier.", body.RootElement.GetProperty("errors").GetProperty("courierId")[0].GetString());
        using var stale = await Send(client, s.ParcelId, s.OtherCourierId, version: 1);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(before, (await s.GraphAsync()).Signature());
        using var pickup = await s.UpdateAsync("PickedUp", 2);
        Assert.Equal(HttpStatusCode.OK, pickup.StatusCode);
        var pickedUp = (await s.GraphAsync()).Signature();
        using var tooLate = await Send(client, s.ParcelId, s.OtherCourierId, version: 3);
        Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
        Assert.Equal(pickedUp, (await s.GraphAsync()).Signature());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-long")]
    public async Task InvalidReasonsDoNotReachStorage(string? reason)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), "Dispatcher"));
        using var client = factory.CreateClient();
        using var response = await Send(client, Guid.NewGuid(), Guid.NewGuid(), reason: reason == "too-long" ? new string('a', 501) : reason);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("pickup")]
    [InlineData("reassignment")]
    [InlineData("midnight")]
    public async Task NormalizedReasonReplaysExactResponseAfterLaterChanges(string later)
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        using var first = await Send(client, s.ParcelId, s.OtherCourierId, key: key, reason: "  Route  blocked  ");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var exact = await first.Content.ReadAsStringAsync();
        if (later == "pickup")
        {
            using var courierFactory = s.CreateFactory(s.OtherCourierId);
            using var courierClient = courierFactory.CreateClient();
            using var pickup = await s.UpdateAsync("PickedUp", 3, client: courierClient);
            Assert.Equal(HttpStatusCode.OK, pickup.StatusCode);
        }
        else if (later == "reassignment")
        {
            s.Clock.UtcNow = s.Clock.UtcNow.AddMinutes(1);
            using var next = await Send(client, s.ParcelId, s.CourierId, version: 3);
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        }
        else s.Clock.UtcNow = s.Clock.UtcNow.AddDays(1);
        var before = (await s.GraphAsync()).Signature();
        using var replay = await Send(client, s.ParcelId, s.OtherCourierId, key: key, reason: "Route  blocked");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(exact, await replay.Content.ReadAsStringAsync());
        Assert.Equal(before, (await s.GraphAsync()).Signature());
        using var conflict = await Send(client, s.ParcelId, s.OtherCourierId, key: key, reason: "Route blocked");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("idempotency_conflict", await conflict.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Courier")]
    [InlineData("Merchant")]
    [InlineData(null)]
    public async Task CommandAuthorizationPrecedesValidation(string? role)
    {
        using var factory = new QuoteApiFactory(null, role is null ? null : QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        using var response = await Send(client, Guid.NewGuid(), Guid.NewGuid(), reason: null);
        Assert.Equal(role is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentReplacementAndPickupHaveOneWinner()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var responses = await Task.WhenAll(Send(client, s.ParcelId, s.OtherCourierId), s.UpdateAsync("PickedUp", 2));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound);
            var graph = await s.GraphAsync();
            Assert.Single(graph.Assignments, a => a.Status == AssignmentStatus.Active);
            Assert.Equal(3, graph.Parcel.Version);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData(false, true, null, 422)]
    [InlineData(true, false, null, 422)]
    [InlineData(true, false, "2026-10-29", 200)]
    public async Task ReplacementEligibilityUsesExistingDailyPolicy(bool active, bool duty, string? date, int status)
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        await using (var db = s.Context())
        {
            (await db.Couriers.SingleAsync(x => x.Id == s.CourierId)).Configure(false, false, 1);
            (await db.Couriers.SingleAsync(x => x.Id == s.OtherCourierId)).Configure(active, duty, 1);
            await db.SaveChangesAsync();
        }
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        using var response = await Send(client, s.ParcelId, s.OtherCourierId, date: date);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Single((await s.GraphAsync()).Assignments, a => a.Status == AssignmentStatus.Active);
    }
    [Theory]
    [InlineData(null, "3a88c70c82fd8f637efb29035e90e2a0713b44f8ae55c6c2f69c7064edba4f13")]
    [InlineData("2026-11-14", "c74a2e3eb8a9878167ea22a6bbfae0bd572ae2f15193ea5b43af86ed0366f796")]
    public void InitialFingerprintMatchesFixedPreP4Vectors(string? date, string fingerprint)
    {
        var input = new AssignCourierInput("11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222", 2, ServiceDate: date);
        var errors = new Dictionary<string, string[]>();
        Assert.Equal(fingerprint, AssignmentValidator.Validate(input, "legacy-key", errors)!.Fingerprint);
        Assert.Empty(errors);
        Assert.Null(AssignmentValidator.Validate(input with { Reason = "not reassignment" }, "key", errors));
        Assert.Contains("reason", errors.Keys);
    }

}
