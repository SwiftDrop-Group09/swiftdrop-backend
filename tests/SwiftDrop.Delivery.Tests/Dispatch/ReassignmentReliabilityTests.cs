using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwiftDrop.Delivery.Api.Application.Dispatch;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Dispatch;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Courier;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Dispatch;

[Collection("Postgres")]
public sealed class ReassignmentReliabilityTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("retirement")]
    [InlineData("parcel")]
    [InlineData("assignment")]
    [InlineData("history")]
    [InlineData("outbox")]
    [InlineData("receipt")]
    [InlineData("commit")]
    public async Task DatabaseFaultAtAnyWriteStageRollsBackAndSameKeyCanRecover(string stage)
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        var actor = Guid.NewGuid();
        using var factory = s.CreateFactory(actor, "Dispatcher");
        using var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString("N");
        var before = (await s.GraphAsync()).Signature();
        var name = "assignment_fault_" + Guid.NewGuid().ToString("N");
        var (table, when, predicate) = stage switch
        {
            "retirement" => ("assignments", "AFTER UPDATE", $"NEW.parcel_id = '{s.ParcelId}'::uuid AND NEW.status = 'Reassigned'"),
            "parcel" => ("parcels", "AFTER UPDATE", $"NEW.id = '{s.ParcelId}'::uuid"),
            "assignment" => ("assignments", "AFTER INSERT", $"NEW.parcel_id = '{s.ParcelId}'::uuid"),
            "history" => ("booking_history", "AFTER INSERT", $"NEW.parcel_id = '{s.ParcelId}'::uuid"),
            "outbox" => ("outbox_messages", "AFTER INSERT", $"NEW.payload->>'parcelId' = '{s.ParcelId}'"),
            _ => ("idempotency_keys", stage == "commit" ? "AFTER INSERT" : "AFTER UPDATE", $"NEW.key = '{key}'")
        };
        await using var db = s.Context();
        // Generated identifiers and UUIDs belong only to this disposable fixture.
        var installSql = $"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN IF {predicate} THEN RAISE EXCEPTION 'fictional private assignment failure'; END IF; RETURN NEW; END $fn$;
            CREATE {(stage == "commit" ? "CONSTRAINT " : "")}TRIGGER {name} {when} ON {table}
            {(stage == "commit" ? "DEFERRABLE INITIALLY DEFERRED" : "")} FOR EACH ROW EXECUTE FUNCTION {name}();
            """;
        await db.Database.ExecuteSqlRawAsync(installSql);
        try
        {
            using var failed = await ReassignmentApiTests.Send(client, s.ParcelId, s.OtherCourierId, key: key);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.DoesNotContain("fictional private", await failed.Content.ReadAsStringAsync());
            Assert.Equal(before, (await s.GraphAsync()).Signature());
            Assert.False(await db.IdempotencyRecords.AnyAsync(x => x.PrincipalId == actor.ToString() && x.Key == key));
        }
        finally
        {
            var cleanupSql = $"DROP TRIGGER {name} ON {table}; DROP FUNCTION {name}();";
            await db.Database.ExecuteSqlRawAsync(cleanupSql);
        }
        using var recovered = await ReassignmentApiTests.Send(client, s.ParcelId, s.OtherCourierId, key: key);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, (await s.GraphAsync()).Assignments.Length);
    }

    [Fact]
    public async Task LostCommitAcknowledgementReplaysCommittedResponseWithoutAnotherEvent()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher", services =>
        {
            services.RemoveAll<IAssignmentStore>();
            services.AddScoped<IAssignmentStore>(provider => new LostAcknowledgementStore(
                new PostgresAssignmentStore(() => provider.GetRequiredService<DeliveryDbContext>())));
        });
        using var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        using var lost = await ReassignmentApiTests.Send(client, s.ParcelId, s.OtherCourierId, key: key);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, lost.StatusCode);
        var committed = await s.GraphAsync();
        Assert.Equal(2, committed.Assignments.Length);
        using var replay = await ReassignmentApiTests.Send(client, s.ParcelId, s.OtherCourierId, key: key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(committed.Signature(), (await s.GraphAsync()).Signature());
        await using var db = s.Context();
        var record = await db.IdempotencyRecords.SingleAsync(x => x.Key == key && x.Operation == AssignmentValidator.Operation);
        using var receipt = JsonDocument.Parse(record.StoredResponse!);
        Assert.Equal(receipt.RootElement.GetProperty("body").GetString(), await replay.Content.ReadAsStringAsync());
    }

    private sealed class LostAcknowledgementStore(IAssignmentStore inner) : IAssignmentStore
    {
        public Task<AssignmentOptions?> GetOptionsAsync(Guid parcelId, DateOnly today, string timeZone, DateOnly? date, int page, int size, bool reassign, CancellationToken ct)
            => inner.GetOptionsAsync(parcelId, today, timeZone, date, page, size, reassign, ct);
        public async Task<IAssignmentTransaction> BeginAsync(CancellationToken ct) => new LostAcknowledgement(await inner.BeginAsync(ct));
    }
    private sealed class LostAcknowledgement(IAssignmentTransaction inner) : IAssignmentTransaction
    {
        public Task<AssignmentState?> LockParcelAsync(Guid id, CancellationToken ct) => inner.LockParcelAsync(id, ct);
        public Task<AssignmentReservation> ReserveAsync(Guid actor, AssignCourierCommand command, DateTimeOffset now, CancellationToken ct) => inner.ReserveAsync(actor, command, now, ct);
        public Task<CourierAssignment?> LockCurrentAssignmentAsync(Guid id, CancellationToken ct) => inner.LockCurrentAssignmentAsync(id, ct);
        public Task RetireAsync(CourierAssignment assignment, CancellationToken ct) => inner.RetireAsync(assignment, ct);
        public Task CheckEligibilityAsync(Parcel parcel, Guid courier, DateOnly date, DateOnly today, CancellationToken ct, Guid? originalCourierId = null) => inner.CheckEligibilityAsync(parcel, courier, date, today, ct, originalCourierId);
        public Task SaveAsync(AssignmentState state, CourierAssignment assignment, BookingHistory history, OutboxMessage outbox, IdempotencyRecord record, CancellationToken ct)
            => inner.SaveAsync(state, assignment, history, outbox, record, ct);
        public async Task CommitAsync(CancellationToken ct)
        { await inner.CommitAsync(ct); throw new AssignmentException(AssignmentFailure.Unavailable); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
