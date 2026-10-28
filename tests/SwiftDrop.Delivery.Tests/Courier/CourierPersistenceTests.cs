using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await using var context = Context();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task OperationalProfileAndUniqueZoneMembershipRoundTrip()
    {
        var seed = await SeedAsync();
        await using var context = Context();
        var actor = new Api.Domain.Courier(Guid.NewGuid(), true, true, 12);
        context.Couriers.Add(actor);
        context.CourierZones.Add(new CourierZone(actor.Id, seed.ZoneId));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var saved = await context.Couriers.SingleAsync(x => x.Id == actor.Id);
        Assert.Equal(12, saved.Capacity);
        Assert.Equal(1, saved.Version);
        Assert.True(await context.CourierZones.AnyAsync(x => x.CourierId == actor.Id && x.ZoneId == seed.ZoneId));
        context.CourierZones.Add(new CourierZone(actor.Id, seed.ZoneId));
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        AssertState(duplicate, PostgresErrorCodes.UniqueViolation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZoneMembershipRequiresBothLocalForeignKeys(bool missingCourier)
    {
        var seed = await SeedAsync();
        await using var context = Context();
        var courier = new Api.Domain.Courier(Guid.NewGuid(), true, true, 10);
        if (!missingCourier)
        {
            context.Couriers.Add(courier);
            await context.SaveChangesAsync();
        }

        context.CourierZones.Add(new CourierZone(courier.Id, missingCourier ? seed.ZoneId : Guid.NewGuid()));
        AssertState(await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync()),
            PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task ZoneMembershipPreventsDeletingItsOperationalProfile()
    {
        var seed = await SeedAsync();
        await using var context = Context();
        var courier = new Api.Domain.Courier(Guid.NewGuid(), true, true, 10);
        context.Couriers.Add(courier);
        context.CourierZones.Add(new CourierZone(courier.Id, seed.ZoneId));
        await context.SaveChangesAsync();

        var deletion = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM couriers WHERE id = {courier.Id}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, deletion.SqlState);
    }

    [Theory]
    [InlineData(ProofType.Photo)]
    [InlineData(ProofType.Signature)]
    public async Task ProofMetadataPersistsWithoutAdvancingParcelOrWritingEvents(ProofType type)
    {
        var seed = await SeedAsync();
        var reference = "proofs/" + Guid.NewGuid().ToString("N");
        var actor = Guid.NewGuid();
        int eventCount;
        await using (var context = Context())
        {
            eventCount = await context.OutboxMessages.CountAsync();
            context.ProofMetadata.Add(new ProofMetadata(Guid.NewGuid(), seed.ParcelId, type,
                reference, "Fictional Recipient", actor, Now));
            await context.SaveChangesAsync();
        }

        await using var verify = Context();
        var saved = await verify.ProofMetadata.SingleAsync(x => x.ParcelId == seed.ParcelId);
        Assert.Equal(type, saved.Type);
        Assert.Equal(reference, saved.StorageRef);
        Assert.Equal(actor, saved.UploadedBy);
        Assert.Equal(Now, saved.UploadedAt);
        Assert.Equal(ParcelStatus.Created, (await verify.Parcels.SingleAsync(x => x.Id == seed.ParcelId)).Status);
        Assert.Equal(1, await verify.BookingHistory.CountAsync(x => x.ParcelId == seed.ParcelId));
        Assert.Equal(eventCount, await verify.OutboxMessages.CountAsync());
        var payload = (await verify.OutboxMessages.SingleAsync(x => x.Id == seed.OutboxId)).Payload;
        Assert.DoesNotContain(reference, payload);
        Assert.DoesNotContain("Fictional Recipient", payload);
    }

    [Fact]
    public async Task ProofRequiresAnExistingParcelAndCannotReuseAnObjectReference()
    {
        await using (var orphan = Context())
        {
            orphan.ProofMetadata.Add(Proof(Guid.NewGuid()));
            AssertState(await Assert.ThrowsAsync<DbUpdateException>(() => orphan.SaveChangesAsync()),
                PostgresErrorCodes.ForeignKeyViolation);
        }

        var first = await SeedAsync();
        var second = await SeedAsync();
        var reference = "proofs/" + Guid.NewGuid().ToString("N");
        await using (var context = Context())
        {
            context.ProofMetadata.Add(Proof(first.ParcelId, reference));
            await context.SaveChangesAsync();
        }

        await using var duplicate = Context();
        duplicate.ProofMetadata.Add(Proof(second.ParcelId, reference));
        AssertState(await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync()),
            PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task DatabaseEnforcesProfileAndProofChecksWhenDomainGuardsAreBypassed()
    {
        var seed = await SeedAsync();
        await using var context = Context();
        var courier = new Api.Domain.Courier(Guid.NewGuid(), true, true, 10);
        var proof = Proof(seed.ParcelId);
        context.AddRange(courier, proof);
        await context.SaveChangesAsync();

        await CheckRejectedAsync(context, $"UPDATE couriers SET capacity = 0 WHERE id = {courier.Id}");
        await CheckRejectedAsync(context, $"UPDATE couriers SET version = 0 WHERE id = {courier.Id}");
        await CheckRejectedAsync(context, $"UPDATE proof_metadata SET type = {"Unknown"} WHERE id = {proof.Id}");
        await CheckRejectedAsync(context, $"UPDATE proof_metadata SET recipient_name = {" "} WHERE id = {proof.Id}");
        await CheckRejectedAsync(context, $"UPDATE proof_metadata SET storage_ref = {"https://public.example.invalid/proof"} WHERE id = {proof.Id}");
        await CheckRejectedAsync(context, $"UPDATE proof_metadata SET uploaded_by = {Guid.Empty} WHERE id = {proof.Id}");
    }

    [Fact]
    public async Task HistoryCanAppendButCannotBeRewrittenOrDeletedThroughEfOrSql()
    {
        var seed = await SeedAsync();
        await using (var context = Context())
        {
            var history = await context.BookingHistory.SingleAsync(x => x.ParcelId == seed.ParcelId);
            context.Entry(history).Property(x => x.DeliveryNote).CurrentValue = "rewritten";
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        }

        using (var context = Context())
        {
            var history = context.BookingHistory.Single(x => x.ParcelId == seed.ParcelId);
            context.BookingHistory.Remove(history);
            Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
        }

        await using var sql = Context();
        await CheckRejectedAsync(sql, $"UPDATE booking_history SET delivery_note = {"rewritten"} WHERE parcel_id = {seed.ParcelId}");
        await CheckRejectedAsync(sql, $"DELETE FROM booking_history WHERE parcel_id = {seed.ParcelId}");
        // Persistence-only fixture: later handlers must append this with the actual state/outbox transaction.
        var next = Guid.NewGuid();
        await sql.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO booking_history (id, parcel_id, from_status, to_status, changed_by, delivery_note, timestamp) VALUES ({next}, {seed.ParcelId}, 'Created', 'Assigned', {Guid.NewGuid()}, NULL, {Now.AddMinutes(1)})");
        Assert.Equal(2, await sql.BookingHistory.CountAsync(x => x.ParcelId == seed.ParcelId));
        Assert.Null((await sql.BookingHistory.SingleAsync(x => x.ParcelId == seed.ParcelId && x.FromStatus == null)).DeliveryNote);
    }

    [Fact]
    public async Task ProfileConcurrentWritesUsingSameVersionHaveExactlyOneWinner()
    {
        var id = Guid.NewGuid();
        await using (var context = Context())
        {
            context.Couriers.Add(new Api.Domain.Courier(id, true, true, 10));
            await context.SaveChangesAsync();
        }

        await using var first = Context();
        await using var second = Context();
        var a = await first.Couriers.SingleAsync(x => x.Id == id);
        var b = await second.Couriers.SingleAsync(x => x.Id == id);
        a.Configure(true, false, 11);
        b.Configure(false, true, 12);
        await AssertOneWinnerAsync(first, second);
        await using var verify = Context();
        Assert.Equal(2, (await verify.Couriers.SingleAsync(x => x.Id == id)).Version);
    }

    [Fact]
    public async Task ExistingParcelVersionTokenRejectsConcurrentPersistenceWrites()
    {
        var seed = await SeedAsync();
        await using var first = Context();
        await using var second = Context();
        var a = await first.Parcels.SingleAsync(x => x.Id == seed.ParcelId);
        var b = await second.Parcels.SingleAsync(x => x.Id == seed.ParcelId);
        // Exercise the existing DB token without implementing a phase-3 workflow handler.
        first.Entry(a).Property(x => x.Version).CurrentValue = 2;
        second.Entry(b).Property(x => x.Version).CurrentValue = 2;
        await AssertOneWinnerAsync(first, second);
        await using var verify = Context();
        Assert.Equal(2, (await verify.Parcels.SingleAsync(x => x.Id == seed.ParcelId)).Version);
    }

    [Fact]
    public async Task AdditiveMigrationPreservesExistingBookingAndLeavesNoPendingModelChanges()
    {
        var schema = "courier_upgrade_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = schema }.ConnectionString;
            await using var context = Context(connection);
            await context.GetService<IMigrator>().MigrateAsync("20261023142857_InitialBookingPersistence");
            var seed = await SeedAsync(context);
            await context.Database.MigrateAsync();
            context.ChangeTracker.Clear();
            Assert.Equal(1, (await context.Parcels.SingleAsync(x => x.Id == seed.ParcelId)).Version);
            Assert.Single(await context.BookingHistory.Where(x => x.ParcelId == seed.ParcelId).ToListAsync());
            Assert.Single(await context.PriceSnapshots.Where(x => x.ParcelId == seed.ParcelId).ToListAsync());
            Assert.Single(await context.OutboxMessages.ToListAsync());
            Assert.False(context.Database.HasPendingModelChanges());
            context.Couriers.Add(new Api.Domain.Courier(Guid.NewGuid(), true, false, 10));
            await context.SaveChangesAsync();
        }
        finally
        {
            // schema is generated above, never a configured/shared schema name.
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private DeliveryDbContext Context(string? connection = null) =>
        new(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(connection ?? fixture.ConnectionString).Options);

    private async Task<Seed> SeedAsync()
    {
        await using var context = Context();
        return await SeedAsync(context);
    }

    private static async Task<Seed> SeedAsync(DeliveryDbContext context)
    {
        var zone = new DeliveryZone(Guid.NewGuid(), "Courier test " + Guid.NewGuid().ToString("N"));
        var tier = new PricingTier(Guid.NewGuid(), zone.Id, ParcelSize.Small, "courier-test-v1", 450m, 60m);
        var parcel = Parcel.ForCustomer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"),
            new ParcelDetails(ParcelSize.Small, 2.5m, zone.Id, "Fictional pickup", null, "Fictional destination", "Fictional contact"), Now);
        var outbox = OutboxMessage.ParcelCreated(Guid.NewGuid(), parcel, "courier-persistence-test");
        context.AddRange(zone, tier, parcel,
            new PriceSnapshot(Guid.NewGuid(), parcel.Id, tier, 450m, 90m, 540m, Now),
            BookingHistory.Created(Guid.NewGuid(), parcel, parcel.CustomerId!.Value),
            outbox);
        await context.SaveChangesAsync();
        return new Seed(zone.Id, parcel.Id, outbox.Id);
    }

    private static ProofMetadata Proof(Guid parcelId, string? reference = null) =>
        new(Guid.NewGuid(), parcelId, ProofType.Photo, reference ?? "proofs/" + Guid.NewGuid().ToString("N"),
            "Fictional Recipient", Guid.NewGuid(), Now);

    private static async Task CheckRejectedAsync(DeliveryDbContext context, FormattableString sql)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(sql));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    private static void AssertState(DbUpdateException error, string state) =>
        Assert.Equal(state, Assert.IsType<PostgresException>(error.InnerException).SqlState);

    private static async Task AssertOneWinnerAsync(DeliveryDbContext first, DeliveryDbContext second)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Exception?> SaveAsync(DeliveryDbContext context)
        {
            await start.Task;
            return await Record.ExceptionAsync(async () => { await context.SaveChangesAsync(); });
        }

        var a = SaveAsync(first);
        var b = SaveAsync(second);
        start.SetResult();
        var outcomes = await Task.WhenAll(a, b);
        Assert.Single(outcomes, x => x is null);
        Assert.IsType<DbUpdateConcurrencyException>(Assert.Single(outcomes, x => x is not null));
    }

    private sealed record Seed(Guid ZoneId, Guid ParcelId, Guid OutboxId);
}
