using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.CourierProof;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierProofCleanupTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DailyPassRemovesOnlyOldUnreferencedObjectsAndKeepsCommittedProof()
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        using var completed = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form());
        Assert.Equal(System.Net.HttpStatusCode.OK, completed.StatusCode);
        var committedRef = Assert.Single(storage.Objects).Key;
        var oldKey = $"proofs/{scenario.ParcelId:N}/{Guid.NewGuid():N}/photo.png";
        await storage.StageAsync(oldKey, new(ProofType.Photo, "image/png", ProofTestSupport.Image()), default);
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddDays(2);
        storage.Now = scenario.Clock.UtcNow;
        var newKey = $"proofs/{scenario.ParcelId:N}/{Guid.NewGuid():N}/signature.png";
        await storage.StageAsync(newKey, new(ProofType.Signature, "image/png", ProofTestSupport.Image()), default);
        await using var context = scenario.Context();
        var cleanup = new ProofCleanup(storage, new PostgresProofCleanupStore(() => context), scenario.Clock);
        Assert.Equal(1, await cleanup.RunOnceAsync(default));
        Assert.False(storage.Objects.ContainsKey(oldKey));
        Assert.True(storage.Objects.ContainsKey(committedRef));
        Assert.True(storage.Objects.ContainsKey(newKey));
        Assert.Equal(0, await cleanup.RunOnceAsync(default));
        Assert.Single(await context.ProofMetadata.Where(x => x.ParcelId == scenario.ParcelId).ToArrayAsync());
    }

    [Fact]
    public async Task CleanupSkipsAnObjectLockedByACompletionTransactionAndProtectsItsLaterCommit()
    {
        var storage = new MemoryProofStorage();
        var checking = new TaskCompletionSource<StoredProof>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.BeforeVerify = async (proof, token) => { checking.TrySetResult(proof); await release.Task.WaitAsync(token); };
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        var completion = ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form());
        try
        {
            var proof = await checking.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // The cleanup clock sees this candidate as old. The completion clock
            // remains fixed, allowing an actual overlap at the reference commit.
            var cleanupClock = new StatusClock { UtcNow = scenario.Clock.UtcNow.AddDays(2) };
            await using var context = scenario.Context();
            var cleanup = new ProofCleanup(storage, new PostgresProofCleanupStore(() => context), cleanupClock);
            Assert.Equal(0, await cleanup.RunOnceAsync(default));
            Assert.True(storage.Objects.ContainsKey(proof.StorageRef));
        }
        finally { release.TrySetResult(); }
        using var response = await completion;
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        await using var after = scenario.Context();
        Assert.Single(await after.ProofMetadata.Where(x => x.ParcelId == scenario.ParcelId).ToArrayAsync());
        var later = new ProofCleanup(storage, new PostgresProofCleanupStore(() => after),
            new StatusClock { UtcNow = scenario.Clock.UtcNow.AddDays(2) });
        Assert.Equal(0, await later.RunOnceAsync(default));
        Assert.Single(storage.Objects);
    }

    [Fact]
    public async Task DatabaseFailureNeverDeletesACandidateWithoutCheckingReferences()
    {
        var storage = new MemoryProofStorage { Now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero) };
        var reference = $"proofs/{Guid.NewGuid():N}/{Guid.NewGuid():N}/photo.png";
        await storage.StageAsync(reference, new(ProofType.Photo, "image/png", ProofTestSupport.Image()), default);
        var cleanup = new ProofCleanup(storage, new PostgresProofCleanupStore(() => throw new InvalidOperationException("private DB failure")),
            new StatusClock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.RunOnceAsync(default));
        Assert.True(storage.Objects.ContainsKey(reference));
    }
}
