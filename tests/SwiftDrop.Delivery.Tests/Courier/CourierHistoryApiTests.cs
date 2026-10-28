using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.DeliveryReads;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierHistoryApiTests(PostgresFixture fixture)
{
    private const string History = "/api/v1/couriers/me/history";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealCompletedAndFailedRecordsUseHistoricalOwnershipAndOnlyCommonParcelFields(bool merchant)
    {
        var actor = Guid.NewGuid();
        var storage = new MemoryProofStorage();
        await using var failed = await StatusTestScenario.CreateAsync(fixture, merchant: merchant, courierId: actor);
        using var failure = await failed.UpdateAsync("Failed", 2, notes: "Private history failure note", reason: "Damaged");
        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        await using var delivered = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery, merchant, actor,
            services => ProofTestSupport.UseStorage(services, storage));
        using var completion = await ProofTestSupport.Send(delivered.Client, delivered.ParcelId, ProofTestSupport.Form());
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        await using var active = await StatusTestScenario.CreateAsync(fixture, courierId: actor);
        await using var unassigned = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created, courierId: actor);
        await using var foreign = await StatusTestScenario.CreateAsync(fixture);
        using var foreignFailure = await foreign.UpdateAsync("Failed", 2, reason: "Other");
        Assert.Equal(HttpStatusCode.OK, foreignFailure.StatusCode);
        var before = await StateAsync();
        failed.Client.DefaultRequestHeaders.Add("X-User-Id", foreign.CourierId.ToString());
        failed.Client.DefaultRequestHeaders.Add("X-User-Role", "Admin");
        using var response = await failed.Client.GetAsync(History);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        using var page = JsonDocument.Parse(body);
        Fields(page.RootElement, "items", "totalCount", "page", "pageSize");
        Assert.Equal(2, page.RootElement.GetProperty("totalCount").GetInt32());
        var items = page.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { failed.ParcelId, delivered.ParcelId }.Order(), items.Select(x => x.GetProperty("id").GetGuid()).Order());
        foreach (var item in items)
        {
            Fields(item, "id", "trackingNumber", "status", "version", "priceSnapshot", "createdAt");
            Fields(item.GetProperty("priceSnapshot"), "total", "currency", "pricingVersion");
            Assert.Equal(540m, item.GetProperty("priceSnapshot").GetProperty("total").GetDecimal());
            Assert.Equal("LKR", item.GetProperty("priceSnapshot").GetProperty("currency").GetString());
        }
        Assert.Equal("Failed", Assert.Single(items, x => x.GetProperty("id").GetGuid() == failed.ParcelId).GetProperty("status").GetString());
        Assert.Equal("Delivered", Assert.Single(items, x => x.GetProperty("id").GetGuid() == delivered.ParcelId).GetProperty("status").GetString());
        foreach (var privateText in new[] { "recipient", "address", "proofs/", "Damaged", "Private history failure note", actor.ToString(), foreign.ParcelId.ToString() })
            Assert.DoesNotContain(privateText, body, StringComparison.OrdinalIgnoreCase);
        using var detail = await failed.Client.GetAsync($"/api/v1/parcels/{failed.ParcelId}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode); // history summary does not reopen private details
        Assert.Equal(before, await StateAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReassignedAwayCourierCannotSeeTheReplacementOutcome(bool delivered)
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        Assert.True(await scenario.ReassignAsync(2));
        using var replacementFactory = scenario.CreateFactory(scenario.OtherCourierId,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        using var replacement = replacementFactory.CreateClient();
        if (delivered)
        {
            var version = 3;
            foreach (var status in new[] { "PickedUp", "InTransit", "OutForDelivery" })
            {
                using var advanced = await scenario.UpdateAsync(status, version++, client: replacement);
                Assert.Equal(HttpStatusCode.OK, advanced.StatusCode);
            }
            using var completed = await ProofTestSupport.Send(replacement, scenario.ParcelId, ProofTestSupport.Form(6));
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }
        else
        {
            using var failed = await scenario.UpdateAsync("Failed", 3, reason: "Other", client: replacement);
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        }
        using var former = await PageAsync(scenario.Client);
        Assert.Equal(0, former.RootElement.GetProperty("totalCount").GetInt32());
        using var owner = await PageAsync(replacement);
        Assert.Equal(scenario.ParcelId, Assert.Single(owner.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(2, (await scenario.GraphAsync()).Assignments.Length);
    }

    [Fact]
    public async Task AssignBackReturnsOneParcelDespiteSeveralHistoricalRows()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        Assert.True(await scenario.ReassignAsync(2));
        Assert.True(await scenario.ReassignAsync(3, scenario.CourierId));
        using var failed = await scenario.UpdateAsync("Failed", 4, reason: "Other");
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        using var page = await PageAsync(scenario.Client);
        Assert.Equal(1, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(scenario.ParcelId, Assert.Single(page.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(3, (await scenario.GraphAsync()).Assignments.Length);
        using var otherFactory = scenario.CreateFactory(scenario.OtherCourierId);
        using var other = otherFactory.CreateClient();
        using var excluded = await PageAsync(other);
        Assert.Equal(0, excluded.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task StablePagesUseCreatedAtAndIdAndKeepTheCompleteScopedCount()
    {
        var scenarios = new List<StatusTestScenario>();
        var actor = Guid.NewGuid();
        try
        {
            for (var index = 0; index < 5; index++)
            {
                var scenario = await StatusTestScenario.CreateAsync(fixture, courierId: actor);
                scenarios.Add(scenario);
                using var failed = await scenario.UpdateAsync("Failed", 2, reason: "Other");
                Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
            }
            await using var context = Context();
            var ids = scenarios.Select(x => x.ParcelId).ToArray();
            var expected = await context.Parcels.Where(x => ids.Contains(x.Id)).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                .Select(x => x.Id).ToArrayAsync();
            var actual = new List<Guid>();
            for (var pageIndex = 1; pageIndex <= 3; pageIndex++)
            {
                using var page = await PageAsync(scenarios[0].Client, $"?page={pageIndex}&pageSize=2");
                Assert.Equal(5, page.RootElement.GetProperty("totalCount").GetInt32());
                actual.AddRange(page.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
            }
            Assert.Equal(expected, actual);
            using var beyond = await PageAsync(scenarios[0].Client, "?page=4&pageSize=2");
            Assert.Equal(5, beyond.RootElement.GetProperty("totalCount").GetInt32());
            Assert.Empty(beyond.RootElement.GetProperty("items").EnumerateArray());
            using var maximum = await PageAsync(scenarios[0].Client, "?pageSize=100");
            Assert.Equal(5, maximum.RootElement.GetProperty("items").GetArrayLength());
        }
        finally { foreach (var scenario in scenarios) await scenario.DisposeAsync(); }
    }

    [Fact]
    public async Task CourierWithoutTerminalAssignmentsGetsAnEmptyNormalPage()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        using var factory = scenario.CreateFactory(Guid.NewGuid());
        using var client = factory.CreateClient();
        using var page = await PageAsync(client);
        Assert.Equal(0, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, page.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(20, page.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Empty(page.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task CountAndPageKeepOneSnapshotWhileAFailureCommitsBetweenThem()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery);
        var audit = new HistoryAudit(async () =>
        {
            using var failed = await scenario.UpdateAsync("Failed", 5, reason: "Other");
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        });
        await using var context = Context(audit);
        var store = new PostgresDeliveryReadStore(() => context);
        var snapshot = await store.GetHistoryAsync(scenario.CourierId, new(1, 20), default);
        Assert.True(audit.Fired);
        Assert.Equal(0, snapshot.TotalCount);
        Assert.Empty(snapshot.Items);
        var subsequent = await store.GetHistoryAsync(scenario.CourierId, new(1, 20), default);
        Assert.Equal(1, subsequent.TotalCount);
        Assert.Equal(scenario.ParcelId, Assert.Single(subsequent.Items).Id);
    }

    [Fact]
    public async Task SqlUsesReadOnlySnapshotAndNeverLoadsPrivateContactHistoryOrProofColumns()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        using var failure = await scenario.UpdateAsync("Failed", 2, reason: "Other");
        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        var before = await StateAsync();
        var audit = new HistoryAudit();
        await using var context = Context(audit);
        var page = await new PostgresDeliveryReadStore(() => context).GetHistoryAsync(scenario.CourierId, new(1, 20), default);
        Assert.Equal(scenario.ParcelId, Assert.Single(page.Items).Id);
        Assert.True(audit.ReadOnly);
        Assert.Equal(System.Data.IsolationLevel.RepeatableRead, audit.Isolation);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(2, audit.Commands.Count);
        foreach (var sql in audit.Commands)
        {
            foreach (var column in new[] { "pickup_address", "dropoff_address", "dropoff_contact", "customer_id", "merchant_id",
                "booking_history", "proof_metadata", "idempotency_keys", "outbox_messages" })
                Assert.DoesNotContain(column, sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("assignments", sql);
        }
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task CancelledAssignmentsAreExcludedEvenThoughTheActorHistoricallyOwnedThem()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        // Test-only representation of the documented owner cancellation. There is
        // no new cancellation API: parcel/assignment/audit/event commit together.
        await using (var context = Context())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var parcel = (await context.Parcels.FromSqlInterpolated($"SELECT * FROM parcels WHERE id = {scenario.ParcelId} FOR UPDATE").ToArrayAsync()).Single();
            var assignment = (await context.Assignments.FromSqlInterpolated(
                $"SELECT *, xmin FROM assignments WHERE parcel_id = {scenario.ParcelId} AND status = 'Active' FOR UPDATE").ToArrayAsync()).Single();
            assignment.MarkCancelled();
            context.Entry(parcel).Property(x => x.Status).CurrentValue = ParcelStatus.Cancelled;
            context.Entry(parcel).Property(x => x.Version).CurrentValue = 3;
            context.Entry(parcel).Property(x => x.UpdatedAt).CurrentValue = scenario.Clock.UtcNow;
            var history = BookingHistory.Created(Guid.NewGuid(), parcel, parcel.CustomerId!.Value);
            context.BookingHistory.Add(history);
            context.Entry(history).Property(x => x.FromStatus).CurrentValue = ParcelStatus.Assigned;
            context.Entry(history).Property(x => x.ToStatus).CurrentValue = ParcelStatus.Cancelled;
            context.Entry(history).Property(x => x.Timestamp).CurrentValue = scenario.Clock.UtcNow;
            context.Entry(history).Property(x => x.ChangeReason).CurrentValue = "Fictional private cancellation";
            var message = OutboxMessage.ParcelCreated(Guid.NewGuid(), parcel, "history-cancellation-fixture");
            var payload = System.Text.Json.Nodes.JsonNode.Parse(message.Payload)!;
            payload["status"] = "Cancelled";
            payload["previousStatus"] = "Assigned";
            payload["occurredAt"] = System.Text.Json.Nodes.JsonValue.Create(scenario.Clock.UtcNow);
            context.OutboxMessages.Add(message);
            context.Entry(message).Property(x => x.Payload).CurrentValue = payload.ToJsonString();
            context.Entry(message).Property(x => x.CreatedAt).CurrentValue = scenario.Clock.UtcNow;
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var before = await StateAsync();
        using var page = await PageAsync(scenario.Client);
        Assert.Equal(0, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task CancelledDatabaseReadDoesNotWriteAnythingOrRetryUnderAnotherActor()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var before = await StateAsync();
        await using var context = Context();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PostgresDeliveryReadStore(() => context).GetHistoryAsync(scenario.CourierId, new(1, 20), cancellation.Token));
        Assert.Equal(before, await StateAsync());
    }

    private DeliveryDbContext Context(DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(fixture.ConnectionString);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    private static async Task<JsonDocument> PageAsync(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync(History + query);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private async Task<string> StateAsync()
    {
        await using var database = Context();
        return JsonSerializer.Serialize(new
        {
            parcels = await database.Parcels.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            assignments = await database.Assignments.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            history = await database.BookingHistory.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            events = await database.OutboxMessages.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            keys = await database.IdempotencyRecords.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            proofs = await database.ProofMetadata.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            quotes = await database.Quotes.CountAsync(), snapshots = await database.PriceSnapshots.CountAsync()
        });
    }

    private static void Fields(JsonElement item, params string[] names) => Assert.Equal(names.Order(StringComparer.Ordinal),
        item.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));

    private sealed class HistoryAudit(Func<Task>? afterCount = null) : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public bool Fired;
        public bool ReadOnly;
        public System.Data.IsolationLevel? Isolation;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            Isolation = command.Transaction?.IsolationLevel;
            await using var probe = command.Connection!.CreateCommand();
            probe.Transaction = command.Transaction;
            probe.CommandText = "SHOW transaction_read_only";
            ReadOnly = (string?)await probe.ExecuteScalarAsync(cancellationToken) == "on";
            return result;
        }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Fired && afterCount is not null && command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase))
            {
                Fired = true;
                await afterCount();
            }
            return result;
        }
    }
}
