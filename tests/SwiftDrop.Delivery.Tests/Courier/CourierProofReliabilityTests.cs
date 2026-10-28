using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Application.CourierStatus;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.CourierStatus;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierProofReliabilityTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("proof")]
    [InlineData("parcel")]
    [InlineData("assignment")]
    [InlineData("history")]
    [InlineData("outbox")]
    [InlineData("receipt")]
    [InlineData("commit")]
    public async Task AnyDatabaseFaultRollsBackAllRowsAndLeavesOnlyAnOrphan(string stage)
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        var before = await ProofTestSupport.Signature(scenario);
        await using (var fault = await DatabaseFault.Install(scenario, stage))
        {
            using var failed = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(), "recovery-key");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.DoesNotContain("private injected proof fault", await failed.Content.ReadAsStringAsync());
            Assert.Equal(before, await ProofTestSupport.Signature(scenario));
            Assert.Single(storage.Objects);
        }
        using var recovered = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(), "recovery-key");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(6, (await scenario.GraphAsync()).Parcel.Version);
        Assert.Equal(2, storage.Objects.Count);
    }

    [Fact]
    public async Task FailureUploadingTheSecondImageLeavesOnlyTheFirstOrphanAndNoDeliveredState()
    {
        var storage = new MemoryProofStorage { FailAtStage = 2 };
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        var before = await ProofTestSupport.Signature(scenario);
        using var response = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(signature: true));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(before, await ProofTestSupport.Signature(scenario));
        Assert.Single(storage.Objects);
    }

    [Fact]
    public async Task UnknownCommitOutcomeReplaysTheCommittedProofWithoutUploadingOrDeletingIt()
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery, configure: services =>
        {
            ProofTestSupport.UseStorage(services, storage);
            services.RemoveAll<ICourierProofStore>();
            services.AddScoped<ICourierProofStore>(provider => new LoseAcknowledgement(
                new PostgresCourierStatusStore(() => provider.GetRequiredService<DeliveryDbContext>())));
        });
        using var uncertain = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, uncertain.StatusCode);
        var committed = await ProofTestSupport.Signature(scenario);
        using var anotherHost = scenario.CreateFactory(configure: services => ProofTestSupport.UseStorage(services, storage));
        using var client = anotherHost.CreateClient();
        using var replay = await ProofTestSupport.Send(client, scenario.ParcelId, ProofTestSupport.Form());
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1, storage.Stages);
        Assert.Single(storage.Objects);
        Assert.Equal(committed, await ProofTestSupport.Signature(scenario));
    }

    [Fact]
    public async Task CancellationAfterStagingLeavesNoDatabaseEffectsAndIsCleanupEligible()
    {
        var storage = new MemoryProofStorage();
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.AfterStage = async (_, token) =>
        {
            staged.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { exited.TrySetResult(); }
        };
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        var before = await ProofTestSupport.Signature(scenario);
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/deliveries/{scenario.ParcelId}/pod")
        { Content = ProofTestSupport.Form() };
        request.Headers.Add("Idempotency-Key", "cancelled-proof");
        var task = scenario.Client.SendAsync(request, cancellation.Token);
        await staged.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(before, await ProofTestSupport.Signature(scenario));
        Assert.Single(storage.Objects);
    }

    private sealed class LoseAcknowledgement(ICourierProofStore inner) : ICourierProofStore
    {
        public async Task<ICourierProofTransaction> BeginProofAsync(CancellationToken cancellationToken) =>
            new LostTransaction(await inner.BeginProofAsync(cancellationToken));
    }

    private sealed class LostTransaction(ICourierProofTransaction inner) : ICourierProofTransaction
    {
        public Task<CourierStatusState?> LoadAuthorizedAsync(Guid courierId, Guid parcelId, CancellationToken cancellationToken) =>
            inner.LoadAuthorizedAsync(courierId, parcelId, cancellationToken);
        public Task<StatusReservation> ReserveProofAsync(Guid courierId, ProofCommand command, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.ReserveProofAsync(courierId, command, now, cancellationToken);
        public Task LockObjectsAsync(IEnumerable<string> refs, CancellationToken cancellationToken) => inner.LockObjectsAsync(refs, cancellationToken);
        public Task SaveProofAsync(CourierStatusState state, IReadOnlyList<ProofMetadata> proofs, BookingHistory history,
            OutboxMessage outbox, IdempotencyRecord record, CancellationToken cancellationToken) =>
            inner.SaveProofAsync(state, proofs, history, outbox, record, cancellationToken);
        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await inner.CommitAsync(cancellationToken);
            throw new CourierStatusStoreException(CourierStatusStorageFailure.Unavailable);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class DatabaseFault(StatusTestScenario scenario, string function, string trigger, string table) : IAsyncDisposable
    {
        public static async Task<DatabaseFault> Install(StatusTestScenario scenario, string stage)
        {
            var (table, condition) = stage switch
            {
                "parcel" => ("parcels", $"NEW.id = '{scenario.ParcelId:D}'::uuid"),
                "assignment" => ("assignments", $"NEW.parcel_id = '{scenario.ParcelId:D}'::uuid"),
                "history" => ("booking_history", $"NEW.parcel_id = '{scenario.ParcelId:D}'::uuid"),
                "outbox" => ("outbox_messages", $"NEW.payload->>'parcelId' = '{scenario.ParcelId:D}'"),
                "receipt" => ("idempotency_keys", $"NEW.parcel_id = '{scenario.ParcelId:D}'::uuid AND NEW.operation = 'CompleteDeliveryWithProof'"),
                "proof" or "commit" => ("proof_metadata", $"NEW.parcel_id = '{scenario.ParcelId:D}'::uuid"),
                _ => throw new ArgumentOutOfRangeException(nameof(stage))
            };
            var suffix = Guid.NewGuid().ToString("N");
            var fault = new DatabaseFault(scenario, "proof_fault_" + suffix, "proof_trigger_" + suffix, table);
            var timing = stage == "commit" ? "CONSTRAINT " : "";
            var deferred = stage == "commit" ? "DEFERRABLE INITIALLY DEFERRED " : "";
            await fault.Execute($"""
                CREATE FUNCTION {fault.Function}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF {condition} THEN RAISE EXCEPTION 'private injected proof fault' USING ERRCODE = '23514'; END IF; RETURN NEW; END $$;
                CREATE {timing}TRIGGER {fault.Trigger} AFTER INSERT OR UPDATE ON {table} {deferred}FOR EACH ROW EXECUTE FUNCTION {fault.Function}();
                """);
            return fault;
        }

        private string Function => function;
        private string Trigger => trigger;
        private async Task Execute(string sql)
        {
            await using var context = scenario.Context();
            await context.Database.OpenConnectionAsync();
            await using var command = context.Database.GetDbConnection().CreateCommand();
            // Fixture-only DDL: table is a switch allowlist, identifiers generated UUIDs,
            // predicate contains only the fixture's typed UUID. No HTTP input is SQL.
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync() => await Execute($"DROP TRIGGER {trigger} ON {table}; DROP FUNCTION {function}();");
    }
}
