using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Bookings.Reliability;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierStatusApiTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegalStagesAtomicallyAdvanceVersionAppendAuditAndKeepEventsPrivate(bool merchant)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, merchant: merchant);
        foreach (var status in new[] { "PickedUp", "InTransit", "OutForDelivery" })
        {
            var before = await scenario.GraphAsync();
            scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddSeconds(1).AddTicks(9);
            using var response = await scenario.UpdateAsync(status, before.Parcel.Version);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.Private);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            Assert.Equal(6, json.RootElement.EnumerateObject().Count());
            Assert.Equal(status, json.RootElement.GetProperty("status").GetString());
            Assert.Equal(before.Parcel.Version + 1, json.RootElement.GetProperty("version").GetInt64());
            Assert.DoesNotContain("private", body, StringComparison.OrdinalIgnoreCase);
            var after = await scenario.GraphAsync();
            Assert.Equal(before.Parcel.Version + 1, after.Parcel.Version);
            Assert.Equal(status, after.Parcel.Status.ToString());
            Assert.Equal(before.History.Length + 1, after.History.Length);
            Assert.Equal(before.Events.Length + 1, after.Events.Length);
            Assert.Equal(before.Keys.Length + 1, after.Keys.Length);
            Assert.Equal(before.Parcel.TrackingNumber, after.Parcel.TrackingNumber);
            Assert.Equal(before.Parcel.CustomerId, after.Parcel.CustomerId);
            Assert.Equal(before.Parcel.MerchantId, after.Parcel.MerchantId);
            Assert.Equal(JsonSerializer.Serialize(before.Snapshot), JsonSerializer.Serialize(after.Snapshot));
            var audit = after.History.Last();
            Assert.Equal(scenario.CourierId, audit.ChangedBy);
            Assert.Equal(before.Parcel.Status, audit.FromStatus);
            Assert.Equal(after.Parcel.Status, audit.ToStatus);
            Assert.Equal("private status note", audit.DeliveryNote);
            Assert.Equal(after.Parcel.UpdatedAt, audit.Timestamp);
            Assert.Equal(0, audit.Timestamp.Ticks % 10);
            var message = after.Events.Single(item => EventVersion(item) == after.Parcel.Version);
            BookingOutboxContractTests.AssertSchema(message.Payload);
            using var payload = JsonDocument.Parse(message.Payload);
            Assert.Equal(before.Parcel.Status.ToString(), payload.RootElement.GetProperty("previousStatus").GetString());
            Assert.Equal(status, payload.RootElement.GetProperty("status").GetString());
            Assert.Equal(after.Parcel.UpdatedAt, payload.RootElement.GetProperty("occurredAt").GetDateTimeOffset());
            Assert.Equal(after.Parcel.MerchantId?.ToString(), payload.RootElement.GetProperty("merchantId").GetString());
            Assert.Null(message.PublishedAt);
            Assert.DoesNotContain("private", message.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(scenario.CourierId.ToString(), message.Payload);
            Assert.Equal(AssignmentStatus.Active, Assert.Single(after.Assignments).Status);
            Assert.NotNull(after.Assignments[0].PickedUpAt);
        }
    }

    [Theory]
    [InlineData(ParcelStatus.Assigned, "InTransit")]
    [InlineData(ParcelStatus.Assigned, "OutForDelivery")]
    [InlineData(ParcelStatus.Assigned, "Assigned")]
    [InlineData(ParcelStatus.Assigned, "Delivered")]
    [InlineData(ParcelStatus.Assigned, "Cancelled")]
    [InlineData(ParcelStatus.PickedUp, "PickedUp")]
    [InlineData(ParcelStatus.PickedUp, "OutForDelivery")]
    [InlineData(ParcelStatus.InTransit, "PickedUp")]
    [InlineData(ParcelStatus.OutForDelivery, "InTransit")]
    [InlineData(ParcelStatus.OutForDelivery, "Delivered")]
    [InlineData(ParcelStatus.Failed, "PickedUp")]
    public async Task InvalidSkipRepeatReverseOrDirectDeliveryHasNoEffects(ParcelStatus source, string target)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, source);
        var before = await scenario.GraphAsync();
        using var response = await scenario.UpdateAsync(target, before.Parcel.Version);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.Conflict, "invalid_transition");
        Assert.Equal(before.Signature(), (await scenario.GraphAsync()).Signature());
    }

    [Fact]
    public async Task StaleVersionReturnsCurrentVersionOnlyAfterOwnershipAuthorization()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var before = await scenario.GraphAsync();
        using var stale = await scenario.UpdateAsync("PickedUp", 1);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(stale, HttpStatusCode.Conflict, "stale_version");
        using var json = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("currentVersion").GetInt64());
        using var otherFactory = scenario.CreateFactory(scenario.OtherCourierId);
        using var other = otherFactory.CreateClient();
        other.DefaultRequestHeaders.Add("X-User-Id", scenario.CourierId.ToString());
        using var denied = await scenario.UpdateAsync("PickedUp", 1, client: other);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(denied, HttpStatusCode.NotFound, "not_found");
        Assert.DoesNotContain("currentVersion", await denied.Content.ReadAsStringAsync());
        Assert.Equal(before.Signature(), (await scenario.GraphAsync()).Signature());
    }

    [Fact]
    public async Task MissingUnassignedAndPreviouslyAssignedCouriersCannotMutate()
    {
        await using var unassigned = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        var beforeUnassigned = await unassigned.GraphAsync();
        using var denied = await unassigned.UpdateAsync("PickedUp", 1);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(beforeUnassigned.Signature(), (await unassigned.GraphAsync()).Signature());
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        Assert.True(await scenario.ReassignAsync(2));
        var before = await scenario.GraphAsync();
        using var former = await scenario.UpdateAsync("PickedUp", 2);
        Assert.Equal(HttpStatusCode.NotFound, former.StatusCode);
        Assert.Equal(before.Signature(), (await scenario.GraphAsync()).Signature());
        using var replacementFactory = scenario.CreateFactory(scenario.OtherCourierId);
        using var replacement = replacementFactory.CreateClient();
        using var success = await scenario.UpdateAsync("PickedUp", before.Parcel.Version, client: replacement);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
    }

    [Theory]
    [InlineData("RecipientUnavailable")]
    [InlineData("AddressUnreachable")]
    [InlineData("Damaged")]
    [InlineData("Other")]
    public async Task FailureReasonIsPrivateAndFailureReleasesAssignmentWithoutReassignment(string reason)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var before = await scenario.GraphAsync();
        var key = Guid.NewGuid().ToString();
        using var response = await scenario.UpdateAsync("Failed", 2, key, notes: null, reason: reason);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var after = await scenario.GraphAsync();
        Assert.Equal(ParcelStatus.Failed, after.Parcel.Status);
        Assert.Equal(3, after.Parcel.Version);
        Assert.Equal(AssignmentStatus.Failed, Assert.Single(after.Assignments).Status);
        Assert.Equal(reason, after.History.Last().ChangeReason);
        Assert.Null(after.History.Last().DeliveryNote);
        Assert.Equal(before.Events.Length + 1, after.Events.Length);
        Assert.DoesNotContain(reason, after.Events.Last().Payload);
        using var replay = await scenario.UpdateAsync("Failed", 2, key, notes: null, reason: reason);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.Equal(after.Signature(), (await scenario.GraphAsync()).Signature());
        using var repeat = await scenario.UpdateAsync("Failed", 3, notes: null, reason: reason);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(repeat, HttpStatusCode.Conflict, "invalid_transition");
        Assert.Equal(after.Signature(), (await scenario.GraphAsync()).Signature());
        using var manifest = await scenario.Client.GetAsync("/api/v1/couriers/me/assignments");
        using var page = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
        Assert.Equal(0, page.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData(ParcelStatus.Assigned)]
    [InlineData(ParcelStatus.PickedUp)]
    [InlineData(ParcelStatus.InTransit)]
    [InlineData(ParcelStatus.OutForDelivery)]
    public async Task FailedIsGuardedFromEveryDocumentedActiveSource(ParcelStatus source)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, source);
        var before = await scenario.GraphAsync();
        using var response = await scenario.UpdateAsync("Failed", before.Parcel.Version, reason: "Damaged");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await scenario.GraphAsync();
        Assert.Equal(before.Parcel.Version + 1, after.Parcel.Version);
        Assert.Equal(AssignmentStatus.Failed, after.Assignments.Single().Status);
        Assert.Equal(source, after.History.Last().FromStatus);
        Assert.Equal("private status note", after.History.Last().DeliveryNote);
    }

    [Fact]
    public async Task SameKeyReplaysOriginalBytesWhileChangedBodyOrParcelConflicts()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var key = Guid.NewGuid().ToString();
        using var first = await scenario.UpdateAsync("PickedUp", 2, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadAsStringAsync();
        var committed = await scenario.GraphAsync();
        scenario.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "retry-correlation");
        using var replay = await scenario.UpdateAsync("PickedUp", 2, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.Equal(committed.Signature(), (await scenario.GraphAsync()).Signature());
        using var changed = await scenario.UpdateAsync("PickedUp", 2, key, notes: "different private note");
        await DeliveryReadAuthenticationTests.AssertProblemAsync(changed, HttpStatusCode.Conflict, "idempotency_conflict");
        await using var another = await StatusTestScenario.CreateAsync(fixture, courierId: scenario.CourierId);
        using var otherParcel = await another.UpdateAsync("PickedUp", 2, key);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(otherParcel, HttpStatusCode.Conflict, "idempotency_conflict");
        Assert.Equal(committed.Signature(), (await scenario.GraphAsync()).Signature());
    }

    [Fact]
    public async Task RetainedKeyExpiresWithoutDeletingItsOriginalResult()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var key = Guid.NewGuid().ToString();
        using var first = await scenario.UpdateAsync("PickedUp", 2, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var before = await scenario.GraphAsync();
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddDays(30);
        using var expired = await scenario.UpdateAsync("PickedUp", 2, key);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(expired, HttpStatusCode.Conflict, "key_expired");
        Assert.Equal(before.Signature(), (await scenario.GraphAsync()).Signature());
    }

    [Fact]
    public async Task SameExpectedVersionWithDifferentKeysCannotBothSucceed()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var before = await scenario.GraphAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Send(string note) { await start.Task; return await scenario.UpdateAsync("PickedUp", 2, notes: note); }
        var a = Send("first private note");
        var b = Send("second private note");
        start.SetResult();
        var results = await Task.WhenAll(a, b);
        using var winner = Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
        using var loser = Assert.Single(results, x => x.StatusCode != HttpStatusCode.OK);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(loser, HttpStatusCode.Conflict, "stale_version");
        var after = await scenario.GraphAsync();
        Assert.Equal(3, after.Parcel.Version);
        Assert.Equal(before.History.Length + 1, after.History.Length);
        Assert.Equal(before.Events.Length + 1, after.Events.Length);
        Assert.Single(after.Keys);
    }

    [Fact]
    public async Task ConcurrentSameKeyRequestsResolveOneLogicalMutation()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var before = await scenario.GraphAsync();
        var key = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => scenario.UpdateAsync("PickedUp", 2, key)));
        string? expected = null;
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                expected ??= body;
                Assert.Equal(expected, body);
            }
        }
        var after = await scenario.GraphAsync();
        Assert.Equal(3, after.Parcel.Version);
        Assert.Equal(before.History.Length + 1, after.History.Length);
        Assert.Equal(before.Events.Length + 1, after.Events.Length);
        Assert.Single(after.Keys);
    }

    [Fact]
    public async Task ReassignmentAndPickupUsingTheSameVersionHaveOneCommitWinner()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var before = await scenario.GraphAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Pickup() { await start.Task; return await scenario.UpdateAsync("PickedUp", 2); }
        async Task<bool> Reassign() { await start.Task; return await scenario.ReassignAsync(2); }
        var pickup = Pickup();
        var assignment = Reassign();
        start.SetResult();
        using var response = await pickup;
        var reassigned = await assignment;
        Assert.NotEqual(reassigned, response.StatusCode == HttpStatusCode.OK);
        if (reassigned) Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Conflict });
        var after = await scenario.GraphAsync();
        Assert.Equal(3, after.Parcel.Version);
        Assert.Equal(before.History.Length + 1, after.History.Length);
        Assert.Equal(before.Events.Length + 1, after.Events.Length);
        var active = Assert.Single(after.Assignments, x => x.Status == AssignmentStatus.Active);
        Assert.Equal(reassigned ? scenario.OtherCourierId : scenario.CourierId, active.CourierId);
        Assert.Equal(reassigned ? ParcelStatus.Assigned : ParcelStatus.PickedUp, after.Parcel.Status);
    }

    internal static long EventVersion(OutboxMessage message)
    {
        using var json = JsonDocument.Parse(message.Payload);
        return json.RootElement.GetProperty("parcelVersion").GetInt64();
    }
}
