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
public sealed class UnassignmentApiTests(PostgresFixture fixture)
{
    private const string Path = "/api/v1/deliveries/unassign";
    private const string WorkloadPath = "/api/v1/couriers/workload";

    private static async Task<HttpResponseMessage> Send(HttpClient client, Guid parcel,
        long version = 2, string? key = null, string? reason = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        { Content = JsonContent.Create(new { parcelId = parcel, expectedVersion = version, reason }) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Dispatcher")]
    [InlineData("Admin")]
    public async Task UnassignRevertsParcelAndCancelsAssignmentWithHistory(string role)
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        var before = await s.GraphAsync();
        var actor = Guid.NewGuid();
        using var factory = s.CreateFactory(actor, role);
        using var client = factory.CreateClient();

        using var response = await Send(client, s.ParcelId, reason: "Courier unavailable");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);

        var body = await response.Content.ReadAsStringAsync();
        using var result = JsonDocument.Parse(body);
        Assert.Equal("Created", result.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, result.RootElement.GetProperty("version").GetInt64());

        var graph = await s.GraphAsync();
        Assert.Equal(ParcelStatus.Created, graph.Parcel.Status);
        Assert.Equal(3, graph.Parcel.Version);

        // Assignment cancelled, not deleted
        var assignment = Assert.Single(graph.Assignments);
        Assert.Equal(AssignmentStatus.Cancelled, assignment.Status);
        Assert.Equal(before.Assignments[0].Id, assignment.Id);

        // History preserved and new entry appended
        Assert.Equal(before.History.Length + 1, graph.History.Length);
        var unassignEntry = graph.History.Last();
        Assert.Equal(ParcelStatus.Assigned, unassignEntry.FromStatus);
        Assert.Equal(ParcelStatus.Created, unassignEntry.ToStatus);
        Assert.Equal(actor, unassignEntry.ChangedBy);
        Assert.Equal("Courier unavailable", unassignEntry.ChangeReason);

        // Outbox event appended
        Assert.Equal(before.Events.Length + 1, graph.Events.Length);
        var outbox = graph.Events.Last();
        using var payload = JsonDocument.Parse(outbox.Payload);
        Assert.Equal("Created", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal("Assigned", payload.RootElement.GetProperty("previousStatus").GetString());
    }

    [Fact]
    public async Task UnassignWithoutReasonIsAccepted()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();

        using var response = await Send(client, s.ParcelId, reason: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var graph = await s.GraphAsync();
        Assert.Equal(ParcelStatus.Created, graph.Parcel.Status);
        Assert.Null(graph.History.Last().ChangeReason);
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("pickup")]
    [InlineData("reassignment")]
    [InlineData("idle")]
    public async Task SameKeyReplaysExactResponseAfterLaterChanges(string later)
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        using var first = await Send(client, s.ParcelId, key: key, reason: "  Route  blocked  ");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var exact = await first.Content.ReadAsStringAsync();

        if (later == "pickup")
        {
            // Parcel reassigned and picked up after the unassignment
            using var assignFactory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
            using var assignClient = assignFactory.CreateClient();
            using var reassign = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deliveries/assign")
            { Content = JsonContent.Create(new { parcelId = s.ParcelId, courierId = s.CourierId, expectedVersion = 3, reassign = false }) };
            reassign.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var ar = await assignClient.SendAsync(reassign);
            Assert.Equal(HttpStatusCode.OK, ar.StatusCode);
            using var pickup = await s.UpdateAsync("PickedUp", 4);
            Assert.Equal(HttpStatusCode.OK, pickup.StatusCode);
        }
        else if (later == "reassignment")
        {
            s.Clock.UtcNow = s.Clock.UtcNow.AddMinutes(1);
            using var assignFactory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
            using var assignClient = assignFactory.CreateClient();
            using var reassign = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deliveries/assign")
            { Content = JsonContent.Create(new { parcelId = s.ParcelId, courierId = s.CourierId, expectedVersion = 3, reassign = false }) };
            reassign.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var ar = await assignClient.SendAsync(reassign);
            Assert.Equal(HttpStatusCode.OK, ar.StatusCode);
        }
        else s.Clock.UtcNow = s.Clock.UtcNow.AddDays(1);

        var before = (await s.GraphAsync()).Signature();
        using var replay = await Send(client, s.ParcelId, key: key, reason: "Route  blocked");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(exact, await replay.Content.ReadAsStringAsync());
        Assert.Equal(before, (await s.GraphAsync()).Signature());
    }

    [Fact]
    public async Task DifferentBodySameKeyIsRejectedAsIdempotencyConflict()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        using var first = await Send(client, s.ParcelId, key: key, reason: "Route blocked");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var conflict = await Send(client, s.ParcelId, key: key, reason: "Different reason");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("idempotency_conflict", await conflict.Content.ReadAsStringAsync());
    }

    // ── Version / state guards ─────────────────────────────────────────────

    [Fact]
    public async Task StaleVersionIsRejectedWithCurrentVersion()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var before = (await s.GraphAsync()).Signature();

        using var stale = await Send(client, s.ParcelId, version: 1);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadAsStringAsync();
        Assert.Contains("stale_version", body);
        Assert.Contains("\"currentVersion\"", body);
        Assert.Equal(before, (await s.GraphAsync()).Signature());
    }

    [Fact]
    public async Task UnassigningCreatedParcelIsInvalidTransition()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();

        using var response = await Send(client, s.ParcelId, version: 1);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("invalid_transition", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(ParcelStatus.PickedUp)]
    [InlineData(ParcelStatus.InTransit)]
    [InlineData(ParcelStatus.OutForDelivery)]
    public async Task UnassigningPostPickupParcelIsInvalidTransition(ParcelStatus status)
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture, status);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var graph = await s.GraphAsync();

        using var response = await Send(client, s.ParcelId, version: graph.Parcel.Version);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("invalid_transition", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnassigningAfterPickupOnSameAssignmentIsRejected()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        using var courierFactory = s.CreateFactory(s.CourierId);
        using var courierClient = courierFactory.CreateClient();
        using var pickup = await s.UpdateAsync("PickedUp", 2, client: courierClient);
        Assert.Equal(HttpStatusCode.OK, pickup.StatusCode);
        var pickedUpSig = (await s.GraphAsync()).Signature();

        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        using var tooLate = await Send(client, s.ParcelId, version: 3);

        Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
        Assert.Equal(pickedUpSig, (await s.GraphAsync()).Signature());
    }

    // ── Concurrent requests ────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentUnassignAndPickupHaveOneWinner()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        using var dispatchFactory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var dispatchClient = dispatchFactory.CreateClient();
        using var courierFactory = s.CreateFactory(s.CourierId);
        using var courierClient = courierFactory.CreateClient();

        var responses = await Task.WhenAll(
            Send(dispatchClient, s.ParcelId),
            s.UpdateAsync("PickedUp", 2, client: courierClient));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound);
            var graph = await s.GraphAsync();
            Assert.Single(graph.Assignments);
            Assert.Equal(3, graph.Parcel.Version);
        }
        finally { foreach (var r in responses) r.Dispose(); }
    }

    [Fact]
    public async Task ConcurrentReassignAndUnassignHaveOneWinner()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        using var dispatchFactory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var dispatchClient = dispatchFactory.CreateClient();

        using var unassignReq = new HttpRequestMessage(HttpMethod.Post, Path)
        { Content = JsonContent.Create(new { parcelId = s.ParcelId, expectedVersion = 2 }) };
        unassignReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var reassignReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deliveries/assign")
        { Content = JsonContent.Create(new { parcelId = s.ParcelId, courierId = s.OtherCourierId, expectedVersion = 2, reassign = true, reason = "Concurrent replacement" }) };
        reassignReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var responses = await Task.WhenAll(
            dispatchClient.SendAsync(unassignReq),
            dispatchClient.SendAsync(reassignReq));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode is HttpStatusCode.Conflict);
            var graph = await s.GraphAsync();
            Assert.Equal(3, graph.Parcel.Version);
        }
        finally { foreach (var r in responses) r.Dispose(); }
    }

    // ── Workload and history views ─────────────────────────────────────────

    [Fact]
    public async Task WorkloadDecrementsAfterUnassignment()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();

        using var beforeWorkload = await client.GetAsync($"/api/v1/couriers/{s.CourierId}/assignments");
        Assert.Equal(HttpStatusCode.OK, beforeWorkload.StatusCode);
        Assert.Contains(s.ParcelId.ToString(), await beforeWorkload.Content.ReadAsStringAsync());

        using var unassign = await Send(client, s.ParcelId);
        Assert.Equal(HttpStatusCode.OK, unassign.StatusCode);

        using var afterWorkload = await client.GetAsync($"/api/v1/couriers/{s.CourierId}/assignments");
        Assert.Equal(HttpStatusCode.OK, afterWorkload.StatusCode);
        Assert.DoesNotContain(s.ParcelId.ToString(), await afterWorkload.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CourierHistoryExcludesCancelledAssignment()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        using var dispatchFactory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var dispatchClient = dispatchFactory.CreateClient();

        using var unassign = await Send(dispatchClient, s.ParcelId);
        Assert.Equal(HttpStatusCode.OK, unassign.StatusCode);

        // Courier history (completed deliveries) should not include cancelled assignment
        using var history = await s.Client.GetAsync("/api/v1/couriers/me/history");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var historyBody = await history.Content.ReadAsStringAsync();
        Assert.DoesNotContain(s.ParcelId.ToString(), historyBody);
    }

    // ── Authorization ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Customer")]
    [InlineData("Courier")]
    [InlineData("Merchant")]
    [InlineData(null)]
    public async Task CommandAuthorizationPrecedesValidation(string? role)
    {
        using var factory = new QuoteApiFactory(null, role is null ? null : QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        using var response = await Send(client, Guid.NewGuid());
        Assert.Equal(role is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Input validation ───────────────────────────────────────────────────

    [Fact]
    public async Task MissingIdempotencyKeyIsRejected()
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), "Dispatcher"));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        { Content = JsonContent.Create(new { parcelId = Guid.NewGuid(), expectedVersion = 1 }) };
        // No Idempotency-Key header
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null, "parcelId")]
    [InlineData("not-a-guid", "parcelId")]
    public async Task InvalidParcelIdIsRejected(string? parcelId, string field)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), "Dispatcher"));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        { Content = JsonContent.Create(new { parcelId, expectedVersion = 1 }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(field, body);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveVersionIsRejected(long version)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), "Dispatcher"));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        { Content = JsonContent.Create(new { parcelId = Guid.NewGuid(), expectedVersion = version }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("expectedVersion", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TooLongReasonIsRejected()
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), "Dispatcher"));
        using var client = factory.CreateClient();
        using var response = await Send(client, Guid.NewGuid(), reason: new string('a', 501));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("reason", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ParcelNotFoundIsRejected()
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), "Dispatcher"));
        using var client = factory.CreateClient();
        using var response = await Send(client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
