using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierFailureReliabilityTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("parcel")]
    [InlineData("assignment")]
    [InlineData("history")]
    [InlineData("outbox")]
    [InlineData("receipt")]
    [InlineData("commit")]
    public async Task FailedWriteOrCommitRestoresActiveAssignmentAndAllAuditReplayRows(string stage)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery);
        var before = await ProofTestSupport.Signature(scenario);
        var key = Guid.NewGuid().ToString("N");
        await using (var fault = await CourierStatusReliabilityTests.WriteFault.InstallAsync(scenario, key, stage))
        {
            using var rejected = await scenario.UpdateAsync("Failed", 5, key, notes: "Private rollback note", reason: "Damaged");
            await DeliveryReadAuthenticationTests.AssertProblemAsync(rejected, HttpStatusCode.ServiceUnavailable, "delivery_unavailable");
            Assert.DoesNotContain("fictional private failure", await rejected.Content.ReadAsStringAsync());
            Assert.Equal(before, await ProofTestSupport.Signature(scenario));
        }
        using var recovery = await scenario.UpdateAsync("Failed", 5, key, notes: "Private rollback note", reason: "Damaged");
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
        var after = await scenario.GraphAsync();
        Assert.Equal(6, after.Parcel.Version);
        Assert.Equal(AssignmentStatus.Failed, Assert.Single(after.Assignments).Status);
        Assert.Single(after.History, x => x.ToStatus == ParcelStatus.Failed);
        Assert.Single(after.Keys);
    }

    [Theory]
    [InlineData("identical")]
    [InlineData("changed-payload")]
    [InlineData("different-key")]
    public async Task CompetingFailuresCannotOverwriteTheWinningReasonOrEmitAnotherEvent(string kind)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery);
        var before = await scenario.GraphAsync();
        var firstKey = Guid.NewGuid().ToString("N");
        var secondKey = kind == "different-key" ? Guid.NewGuid().ToString("N") : firstKey;
        var secondReason = kind == "identical" ? "RecipientUnavailable" : "AddressUnreachable";
        var secondNote = kind == "identical" ? "First private reason" : "Second private reason";
        var firstTask = scenario.UpdateAsync("Failed", 5, firstKey, notes: "First private reason", reason: "RecipientUnavailable");
        var secondTask = scenario.UpdateAsync("Failed", 5, secondKey, notes: secondNote, reason: secondReason);
        using var first = await firstTask;
        using var second = await secondTask;
        if (kind == "identical")
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        }
        else
        {
            Assert.Equal(new[] { 200, 409 }, new[] { (int)first.StatusCode, (int)second.StatusCode }.Order().ToArray());
            var loser = first.StatusCode == HttpStatusCode.Conflict ? first : second;
            await DeliveryReadAuthenticationTests.AssertProblemAsync(loser, HttpStatusCode.Conflict,
                kind == "changed-payload" ? "idempotency_conflict" : "stale_version");
        }
        var after = await scenario.GraphAsync();
        Assert.Equal(6, after.Parcel.Version);
        Assert.Equal(before.Events.Length + 1, after.Events.Length);
        var history = Assert.Single(after.History, x => x.ToStatus == ParcelStatus.Failed);
        Assert.Equal(first.StatusCode == HttpStatusCode.OK ? "RecipientUnavailable" : secondReason, history.ChangeReason);
        Assert.Equal(first.StatusCode == HttpStatusCode.OK ? "First private reason" : secondNote, history.DeliveryNote);
        Assert.Equal(AssignmentStatus.Failed, Assert.Single(after.Assignments).Status);
        Assert.Single(after.Keys);
        Assert.DoesNotContain("private reason", after.Events[^1].Payload);
    }

    [Fact]
    public async Task LostFailureResponseReplaysFromAnotherHostAfterTheAssignmentHasClosed()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        using var transport = new CourierStatusReliabilityTests.LoseResponse();
        using var client = scenario.Factory.CreateDefaultClient(transport);
        var key = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<HttpRequestException>(() => scenario.UpdateAsync("Failed", 2, key,
            notes: null, reason: "Other", client: client));
        var committed = await scenario.GraphAsync();
        Assert.Equal(ParcelStatus.Failed, committed.Parcel.Status);
        Assert.Equal(AssignmentStatus.Failed, Assert.Single(committed.Assignments).Status);
        var stored = Assert.Single(committed.Keys);
        using var envelope = JsonDocument.Parse(stored.StoredResponse!);
        using var anotherHost = scenario.CreateFactory();
        using var retryClient = anotherHost.CreateClient();
        using var replay = await scenario.UpdateAsync("Failed", 2, key, notes: null, reason: "Other", client: retryClient);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(envelope.RootElement.GetProperty("body").GetString(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(committed.Signature(), (await scenario.GraphAsync()).Signature());
        await using var database = scenario.Context();
        Assert.False(await database.Assignments.AnyAsync(x => x.ParcelId == scenario.ParcelId && x.Status == AssignmentStatus.Active));
    }
}
