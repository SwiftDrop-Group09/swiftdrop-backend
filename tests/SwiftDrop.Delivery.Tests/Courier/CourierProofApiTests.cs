using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Bookings.Reliability;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierProofApiTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task CompletesWithPhotoSignatureOrBothAndAtomicallyAppendsPrivateMetadata(bool photo, bool signature, bool jpeg)
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        var before = await scenario.GraphAsync();
        using var response = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId,
            ProofTestSupport.Form(photo: photo, signature: signature, jpeg: jpeg));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
        var body = await response.Content.ReadAsStringAsync();
        using var receipt = JsonDocument.Parse(body);
        Assert.Equal(6, receipt.RootElement.EnumerateObject().Count());
        Assert.Equal("Delivered", receipt.RootElement.GetProperty("status").GetString());
        Assert.Equal(6, receipt.RootElement.GetProperty("version").GetInt64());
        Assert.DoesNotContain("Private recipient", body);
        Assert.DoesNotContain("proofs/", body);
        var after = await scenario.GraphAsync();
        Assert.Equal(ParcelStatus.Delivered, after.Parcel.Status);
        Assert.Equal(6, after.Parcel.Version);
        Assert.Equal(before.Parcel.TrackingNumber, after.Parcel.TrackingNumber);
        Assert.Equal(before.Snapshot.Total, after.Snapshot.Total);
        var assignment = Assert.Single(after.Assignments);
        Assert.Equal(AssignmentStatus.Delivered, assignment.Status);
        Assert.Equal(scenario.Clock.UtcNow, assignment.DeliveredAt);
        Assert.Equal(before.History.Length + 1, after.History.Length);
        var history = Assert.Single(after.History, x => x.ToStatus == ParcelStatus.Delivered);
        Assert.Equal(ParcelStatus.OutForDelivery, history.FromStatus);
        Assert.Equal(scenario.CourierId, history.ChangedBy);
        Assert.Null(history.DeliveryNote);
        Assert.Equal(before.Events.Length + 1, after.Events.Length);
        var emitted = after.Events[^1];
        using var payload = JsonDocument.Parse(emitted.Payload);
        BookingOutboxContractTests.AssertSchema(emitted.Payload);
        Assert.Equal(6, payload.RootElement.GetProperty("parcelVersion").GetInt64());
        Assert.DoesNotContain("Private recipient", emitted.Payload);
        Assert.DoesNotContain("proofs/", emitted.Payload);
        await using var context = scenario.Context();
        var proofs = await context.ProofMetadata.AsNoTracking().Where(x => x.ParcelId == scenario.ParcelId).ToArrayAsync();
        Assert.Equal((photo ? 1 : 0) + (signature ? 1 : 0), proofs.Length);
        foreach (var proof in proofs)
        {
            Assert.Equal("Private recipient", proof.RecipientName);
            Assert.Equal(scenario.CourierId, proof.UploadedBy);
            Assert.StartsWith($"proofs/{scenario.ParcelId:N}/", proof.StorageRef);
            Assert.DoesNotContain("untrusted", proof.StorageRef);
            Assert.True(storage.Objects.ContainsKey(proof.StorageRef));
        }
        var key = await context.IdempotencyRecords.SingleAsync(x => x.PrincipalId == scenario.CourierId.ToString("D") && x.Operation == ProofValidator.Operation);
        using var stored = JsonDocument.Parse(key.StoredResponse!);
        Assert.Equal(body, stored.RootElement.GetProperty("body").GetString());
        using var manifest = await scenario.Client.GetAsync("/api/v1/couriers/me/assignments");
        Assert.DoesNotContain(scenario.ParcelId.ToString(), await manifest.Content.ReadAsStringAsync());
        // Completion closes current detail access; it does not grant a download.
        using var detail = await scenario.Client.GetAsync($"/api/v1/parcels/{scenario.ParcelId}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        using var direct = await scenario.Client.GetAsync($"/proofs/{scenario.ParcelId:N}");
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
    }

    [Theory]
    [InlineData(ParcelStatus.Assigned, 2)]
    [InlineData(ParcelStatus.PickedUp, 3)]
    [InlineData(ParcelStatus.InTransit, 4)]
    public async Task CompletionRejectsEveryEarlierActiveStageWithoutUploading(ParcelStatus source, long version)
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, source,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        var before = await ProofTestSupport.Signature(scenario);
        using var response = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(version));
        await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.Conflict, "invalid_transition");
        Assert.Equal(0, storage.Stages);
        Assert.Equal(before, await ProofTestSupport.Signature(scenario));
    }

    [Fact]
    public async Task StaleOwnVersionIs409ButAnotherCourierReceivesNoVersionAndNoUpload()
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        var before = await ProofTestSupport.Signature(scenario);
        using var stale = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(4));
        await DeliveryReadAuthenticationTests.AssertProblemAsync(stale, HttpStatusCode.Conflict, "stale_version");
        using var error = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal(5, error.RootElement.GetProperty("currentVersion").GetInt64());
        using var otherHost = scenario.CreateFactory(scenario.OtherCourierId, configure: services => ProofTestSupport.UseStorage(services, storage));
        using var other = otherHost.CreateClient();
        using var foreign = await ProofTestSupport.Send(other, scenario.ParcelId, ProofTestSupport.Form(4));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.DoesNotContain("currentVersion", await foreign.Content.ReadAsStringAsync());
        Assert.Equal(0, storage.Stages);
        Assert.Equal(before, await ProofTestSupport.Signature(scenario));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnassignedOrFormerCourierCannotUpload(bool reassign)
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, reassign ? ParcelStatus.Assigned : ParcelStatus.Created,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        if (reassign) Assert.True(await scenario.ReassignAsync(2));
        var before = await ProofTestSupport.Signature(scenario);
        using var response = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(2));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, storage.Stages);
        Assert.Equal(before, await ProofTestSupport.Signature(scenario));
    }

    [Fact]
    public async Task ExactReplaySurvivesTerminalAssignmentAndStorageOutageWithoutAnyNewUpload()
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        using var first = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(), "same");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var original = await first.Content.ReadAsStringAsync();
        var committed = await ProofTestSupport.Signature(scenario);
        storage.FailStage = true;
        storage.FailVerify = true;
        using var replay = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(), "same");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, storage.Stages);
        Assert.Equal(committed, await ProofTestSupport.Signature(scenario));
        using var repeat = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(6), "different");
        await DeliveryReadAuthenticationTests.AssertProblemAsync(repeat, HttpStatusCode.Conflict, "invalid_transition");
        Assert.Equal(committed, await ProofTestSupport.Signature(scenario));
        using var changed = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(name: "Changed"), "same");
        await DeliveryReadAuthenticationTests.AssertProblemAsync(changed, HttpStatusCode.Conflict, "idempotency_conflict");
        using var contentChanged = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId,
            ProofTestSupport.Form(photoBytes: ProofTestSupport.Image(shade: 20)), "same");
        Assert.Equal(HttpStatusCode.Conflict, contentChanged.StatusCode);
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddDays(30);
        using var expired = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(), "same");
        await DeliveryReadAuthenticationTests.AssertProblemAsync(expired, HttpStatusCode.Conflict, "key_expired");
        Assert.Equal(committed, await ProofTestSupport.Signature(scenario));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("stage")]
    [InlineData("expired")]
    [InlineData("wrong-parcel-reference")]
    public async Task UnconfirmedExpiredOrMismatchedStorageCanNeverRecordDelivered(string failure)
    {
        var storage = new MemoryProofStorage
        {
            FailStage = failure == "stage", Expired = failure == "expired", WrongReference = failure == "wrong-parcel-reference"
        };
        if (failure is "missing" or "corrupt") storage.AfterStage = (proof, token) =>
        {
            if (failure == "missing") storage.Objects.TryRemove(proof.StorageRef, out _);
            else storage.Objects[proof.StorageRef] = (proof, "corrupted bytes"u8.ToArray());
            return Task.CompletedTask;
        };
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        var before = await ProofTestSupport.Signature(scenario);
        using var response = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form());
        Assert.Equal(failure == "expired" ? HttpStatusCode.BadRequest : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(before, await ProofTestSupport.Signature(scenario));
    }

    [Fact]
    public async Task UploadThatExpiresDuringStorageVerificationCannotBeCommitted()
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        var before = await ProofTestSupport.Signature(scenario);
        storage.BeforeVerify = (proof, token) =>
        {
            scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddDays(2);
            return Task.CompletedTask;
        };
        using var response = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await ProofTestSupport.Signature(scenario));
    }

    [Fact]
    public async Task DecodeFailureLeavesNoUploadedObjectsOrPartialDatabaseRecords()
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        var before = await ProofTestSupport.Signature(scenario);
        var form = ProofTestSupport.Form();
        ProofTestSupport.AddFile(form, "signature", "MZ executable"u8.ToArray(), "image/png");
        using var response = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(storage.Objects);
        Assert.Equal(before, await ProofTestSupport.Signature(scenario));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentCompletionCommitsOnlyOneEventAndReceipt(bool sameKey)
    {
        var storage = new MemoryProofStorage();
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        storage.AfterStage = async (_, token) =>
        {
            if (Interlocked.Increment(ref arrivals) == 2) uploaded.TrySetResult();
            await released.Task.WaitAsync(token);
        };
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        var before = await scenario.GraphAsync();
        var first = ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(), "first");
        var second = ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(), sameKey ? "first" : "second");
        try { await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { released.TrySetResult(); }
        using var one = await first;
        using var two = await second;
        if (sameKey)
        {
            Assert.Equal(HttpStatusCode.OK, one.StatusCode);
            Assert.Equal(HttpStatusCode.OK, two.StatusCode);
            Assert.Equal(await one.Content.ReadAsStringAsync(), await two.Content.ReadAsStringAsync());
        }
        else Assert.Equal(new[] { 200, 409 }, new[] { (int)one.StatusCode, (int)two.StatusCode }.Order().ToArray());
        var after = await scenario.GraphAsync();
        Assert.Equal(6, after.Parcel.Version);
        Assert.Equal(before.Events.Length + 1, after.Events.Length);
        await using var context = scenario.Context();
        Assert.Single(await context.ProofMetadata.Where(x => x.ParcelId == scenario.ParcelId).ToArrayAsync());
        Assert.Single(await context.IdempotencyRecords.Where(x => x.PrincipalId == scenario.CourierId.ToString("D")
            && x.Operation == ProofValidator.Operation).ToArrayAsync());
        Assert.Equal(2, storage.Objects.Count); // loser is an orphan, not another committed proof
    }

    [Fact]
    public async Task StateChangeAfterUploadWinsOverCompletionAndLeavesOnlyACleanupCandidate()
    {
        var storage = new MemoryProofStorage();
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.AfterStage = async (_, token) => { staged.TrySetResult(); await release.Task.WaitAsync(token); };
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        var completion = ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form());
        try
        {
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var failed = await scenario.UpdateAsync("Failed", 5, reason: "RecipientUnavailable");
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        }
        finally { release.TrySetResult(); }
        using var response = await completion;
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // active assignment was closed
        var graph = await scenario.GraphAsync();
        Assert.Equal(ParcelStatus.Failed, graph.Parcel.Status);
        await using var context = scenario.Context();
        Assert.Empty(await context.ProofMetadata.Where(x => x.ParcelId == scenario.ParcelId).ToArrayAsync());
        Assert.Empty(await context.IdempotencyRecords.Where(x => x.PrincipalId == scenario.CourierId.ToString("D")
            && x.Operation == ProofValidator.Operation).ToArrayAsync());
        Assert.Single(storage.Objects);
    }
}
