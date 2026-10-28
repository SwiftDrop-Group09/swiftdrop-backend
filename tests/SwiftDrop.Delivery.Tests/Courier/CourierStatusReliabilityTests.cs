using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwiftDrop.Delivery.Api.Application.CourierStatus;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.CourierStatus;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierStatusReliabilityTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("parcel")]
    [InlineData("assignment")]
    [InlineData("history")]
    [InlineData("outbox")]
    [InlineData("receipt")]
    [InlineData("commit")]
    public async Task DatabaseFaultAtAnyWriteOrCommitRollsBackTheEntireTransition(string stage)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var key = Guid.NewGuid().ToString("N");
        var before = await scenario.GraphAsync();
        await using (var fault = await WriteFault.InstallAsync(scenario, key, stage))
        {
            using var response = await scenario.UpdateAsync("PickedUp", 2, key);
            await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "delivery_unavailable");
            Assert.DoesNotContain("fictional private failure", await response.Content.ReadAsStringAsync());
            Assert.Equal(before.Signature(), (await scenario.GraphAsync()).Signature());
        }

        using var recovered = await scenario.UpdateAsync("PickedUp", 2, key);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var after = await scenario.GraphAsync();
        Assert.Equal(3, after.Parcel.Version);
        Assert.Equal(before.Events.Length + 1, after.Events.Length);
        Assert.Single(after.Keys);
    }

    [Fact]
    public async Task ParcelLockWaitIsBoundedAndRetryDoesNotLeaveAReservation()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var before = await scenario.GraphAsync();
        var key = Guid.NewGuid().ToString();
        await using (var holder = scenario.Context())
        await using (var transaction = await holder.Database.BeginTransactionAsync())
        {
            await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM parcels WHERE id = {scenario.ParcelId} FOR UPDATE");
            using var response = await scenario.UpdateAsync("PickedUp", 2, key);
            await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.Conflict, "operation_in_progress");
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
            await transaction.RollbackAsync();
        }

        Assert.Equal(before.Signature(), (await scenario.GraphAsync()).Signature());
        using var recovered = await scenario.UpdateAsync("PickedUp", 2, key);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task CancelledBlockedRequestRollsBackAndAllowsTheSameKeyLater()
    {
        var observation = new Observation();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, configure: services => Observe(services, observation));
        var before = await scenario.GraphAsync();
        var key = Guid.NewGuid().ToString();
        await using (var holder = scenario.Context())
        await using (var transaction = await holder.Database.BeginTransactionAsync())
        {
            await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM parcels WHERE id = {scenario.ParcelId} FOR UPDATE");
            using var cancellation = new CancellationTokenSource();
            using var request = new HttpRequestMessage(HttpMethod.Put, scenario.Path)
            {
                Content = JsonContent.Create(new CourierStatusInput("PickedUp", 2, "private cancelled note"))
            };
            request.Headers.Add("Idempotency-Key", key);
            var pending = scenario.Client.SendAsync(request, cancellation.Token);
            await observation.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await observation.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await transaction.RollbackAsync();
        }
        Assert.Equal(before.Signature(), (await scenario.GraphAsync()).Signature());
        using var retry = await scenario.UpdateAsync("PickedUp", 2, key);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task LostCommitAcknowledgementIsResolvedByTheOriginalStoredResult()
    {
        var observation = new Observation { LoseCommitAcknowledgement = true };
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, configure: services => Observe(services, observation));
        var key = Guid.NewGuid().ToString();
        using var uncertain = await scenario.UpdateAsync("PickedUp", 2, key);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, uncertain.StatusCode);
        var committed = await scenario.GraphAsync();
        Assert.Equal(3, committed.Parcel.Version);
        using var record = JsonDocument.Parse(Assert.Single(committed.Keys).StoredResponse!);
        var body = record.RootElement.GetProperty("body").GetString();
        using var anotherHost = scenario.CreateFactory();
        using var client = anotherHost.CreateClient();
        using var replay = await scenario.UpdateAsync("PickedUp", 2, key, client: client);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.Equal(committed.Signature(), (await scenario.GraphAsync()).Signature());
        Assert.Equal(1, observation.Commits);
    }

    [Fact]
    public async Task LostHttpResponseCanReplayFromAnotherRequestWithoutAnotherEvent()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        using var transport = new LoseResponse();
        using var client = scenario.Factory.CreateDefaultClient(transport);
        var key = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<HttpRequestException>(() => scenario.UpdateAsync("PickedUp", 2, key, client: client));
        var committed = await scenario.GraphAsync();
        using var stored = JsonDocument.Parse(Assert.Single(committed.Keys).StoredResponse!);
        using var replay = await scenario.UpdateAsync("PickedUp", 2, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(stored.RootElement.GetProperty("body").GetString(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(committed.Signature(), (await scenario.GraphAsync()).Signature());
    }

    private static void Observe(IServiceCollection services, Observation observation)
    {
        services.RemoveAll<ICourierStatusStore>();
        services.AddScoped<ICourierStatusStore>(provider => new ObservedStore(
            new PostgresCourierStatusStore(() => provider.GetRequiredService<DeliveryDbContext>()), observation));
    }

    private sealed class Observation
    {
        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool LoseCommitAcknowledgement { get; init; }
        public int Commits;
    }

    private sealed class ObservedStore(ICourierStatusStore inner, Observation observation) : ICourierStatusStore
    {
        public async Task<ICourierStatusTransaction> BeginAsync(CancellationToken cancellationToken) =>
            new ObservedTransaction(await inner.BeginAsync(cancellationToken), observation);
    }

    private sealed class ObservedTransaction(ICourierStatusTransaction inner, Observation observation) : ICourierStatusTransaction
    {
        public async Task<CourierStatusState?> LoadAuthorizedAsync(Guid courierId, Guid parcelId, CancellationToken cancellationToken)
        {
            observation.LoadStarted.TrySetResult();
            return await inner.LoadAuthorizedAsync(courierId, parcelId, cancellationToken);
        }
        public Task<StatusReservation> ReserveAsync(Guid courierId, CourierStatusCommand command, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.ReserveAsync(courierId, command, now, cancellationToken);
        public Task SaveAsync(CourierStatusState state, BookingHistory history, OutboxMessage outbox, IdempotencyRecord reservation,
            CancellationToken cancellationToken) => inner.SaveAsync(state, history, outbox, reservation, cancellationToken);
        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await inner.CommitAsync(cancellationToken);
            if (Interlocked.Increment(ref observation.Commits) == 1 && observation.LoseCommitAcknowledgement)
                throw new CourierStatusStoreException(CourierStatusStorageFailure.Unavailable);
        }
        public async ValueTask DisposeAsync()
        {
            try { await inner.DisposeAsync(); }
            finally { observation.Disposed.TrySetResult(); }
        }
    }

    internal sealed class LoseResponse : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Put && response.StatusCode == HttpStatusCode.OK)
            {
                response.Dispose();
                throw new HttpRequestException("Fictional committed status response was lost.");
            }
            return response;
        }
    }

    internal sealed class WriteFault(StatusTestScenario scenario, string name, string table) : IAsyncDisposable
    {
        public static async Task<WriteFault> InstallAsync(StatusTestScenario scenario, string key, string stage)
        {
            if (!Guid.TryParseExact(key, "N", out _)) throw new ArgumentException("The fixture key must be generated as a UUID.", nameof(key));
            var (table, operation, predicate) = stage switch
            {
                "parcel" => ("parcels", "UPDATE", $"NEW.id = '{scenario.ParcelId}'::uuid"),
                "assignment" => ("assignments", "UPDATE", $"NEW.parcel_id = '{scenario.ParcelId}'::uuid"),
                "history" => ("booking_history", "INSERT", $"NEW.parcel_id = '{scenario.ParcelId}'::uuid"),
                "outbox" => ("outbox_messages", "INSERT", $"NEW.payload->>'parcelId' = '{scenario.ParcelId}'"),
                "receipt" or "commit" => ("idempotency_keys", "UPDATE", $"NEW.key = '{key}' AND NEW.principal_id = '{scenario.CourierId}'"),
                _ => throw new ArgumentOutOfRangeException(nameof(stage))
            };
            var name = "status_fault_" + Guid.NewGuid().ToString("N");
            var trigger = stage == "commit"
                ? $"CREATE CONSTRAINT TRIGGER {name} AFTER UPDATE ON {table} DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION {name}();"
                : $"CREATE TRIGGER {name} BEFORE {operation} ON {table} FOR EACH ROW EXECUTE FUNCTION {name}();";
            // Identifiers, key and resource IDs are generated only in this disposable fixture.
            var sql = $$"""
                CREATE FUNCTION {{name}}() RETURNS trigger LANGUAGE plpgsql AS $fault$
                BEGIN
                    IF {{predicate}} THEN RAISE EXCEPTION 'fictional private failure' USING ERRCODE = '23514'; END IF;
                    RETURN NEW;
                END;
                $fault$;
                {{trigger}}
                """;
            await ExecuteAsync(scenario, sql);
            return new WriteFault(scenario, name, table);
        }
        public async ValueTask DisposeAsync()
        {
            await ExecuteAsync(scenario, $"DROP TRIGGER IF EXISTS {name} ON {table}; DROP FUNCTION IF EXISTS {name}();");
        }

        private static async Task ExecuteAsync(StatusTestScenario scenario, string sql)
        {
            // Dynamic DDL identifiers cannot be parameters. All names are generated
            // UUID identifiers or the hardcoded table whitelist above, never API input.
            await using var context = scenario.Context();
            await context.Database.OpenConnectionAsync();
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }
}
