using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class AssignmentPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly ServiceDate = new(2026, 10, 28);

    public async Task InitializeAsync()
    {
        await using var context = Context();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AssignmentRetainsServiceDayOwnerAndNullableTimes()
    {
        var seed = await SeedAsync();
        await using var context = Context();
        var assignment = NewAssignment(seed.ParcelId, seed.CourierA);
        context.Assignments.Add(assignment);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var saved = await context.Assignments.SingleAsync(x => x.Id == assignment.Id);
        Assert.Equal(seed.ParcelId, saved.ParcelId);
        Assert.Equal(seed.CourierA, saved.CourierId);
        Assert.Equal(ServiceDate, saved.ServiceDate);
        Assert.Equal(Now, saved.AssignedAt);
        Assert.Equal(AssignmentStatus.Active, saved.Status);
        Assert.Null(saved.PickedUpAt);
        Assert.Null(saved.DeliveredAt);
        Assert.NotEqual(0u, context.Entry(saved).Property<uint>("xmin").CurrentValue);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AssignmentRequiresExistingLocalParcelAndCourier(bool missingParcel)
    {
        var seed = await SeedAsync();
        await using var context = Context();
        context.Assignments.Add(NewAssignment(missingParcel ? Guid.NewGuid() : seed.ParcelId,
            missingParcel ? seed.CourierA : Guid.NewGuid()));
        AssertState(await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync()),
            PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task ParcelCannotHaveTwoActiveAssignmentsEvenForTheSameCourier()
    {
        var seed = await SeedAsync();
        await using (var context = Context())
        {
            context.Assignments.Add(NewAssignment(seed.ParcelId, seed.CourierA));
            await context.SaveChangesAsync();
        }

        foreach (var courier in new[] { seed.CourierA, seed.CourierB })
        {
            await using var duplicate = Context();
            duplicate.Assignments.Add(NewAssignment(seed.ParcelId, courier));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
            AssertState(error, PostgresErrorCodes.UniqueViolation);
            Assert.Equal("ux_assignments_current_parcel", ((PostgresException)error.InnerException!).ConstraintName);
        }
    }

    [Fact]
    public async Task ReassignmentRetainsPreviousOwnerAndCurrentDoesNotMeanLatestTimestamp()
    {
        var seed = await SeedAsync();
        var oldId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        await using (var context = Context())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            // Deliberately misleading timestamps prove that the status predicate is authoritative.
            var previous = new CourierAssignment(oldId, seed.ParcelId, seed.CourierA, ServiceDate, Now.AddDays(1));
            context.Assignments.Add(previous);
            await context.SaveChangesAsync();
            previous.MarkReassigned();
            await context.SaveChangesAsync();
            context.Assignments.Add(new CourierAssignment(currentId, seed.ParcelId, seed.CourierB,
                ServiceDate.AddDays(1), Now));
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using var verify = Context();
        var current = await verify.Assignments.SingleAsync(x => x.ParcelId == seed.ParcelId && x.Status == AssignmentStatus.Active);
        Assert.Equal(currentId, current.Id);
        Assert.Equal(seed.CourierB, current.CourierId);
        Assert.Equal(ServiceDate.AddDays(1), current.ServiceDate);
        Assert.False(await verify.Assignments.AnyAsync(x => x.ParcelId == seed.ParcelId && x.CourierId == seed.CourierA && x.Status == AssignmentStatus.Active));
        var old = await verify.Assignments.SingleAsync(x => x.Id == oldId);
        Assert.Equal(AssignmentStatus.Reassigned, old.Status);
        Assert.Equal(seed.CourierA, old.CourierId);
        Assert.Equal(ServiceDate, old.ServiceDate);
        Assert.Equal(2, await verify.Assignments.CountAsync(x => x.ParcelId == seed.ParcelId));
    }

    [Theory]
    [InlineData(AssignmentStatus.Delivered)]
    [InlineData(AssignmentStatus.Failed)]
    [InlineData(AssignmentStatus.Cancelled)]
    public async Task TerminalAssignmentsRemainHistoricalAndLeaveTheActiveSet(AssignmentStatus status)
    {
        var seed = await SeedAsync();
        var assignment = NewAssignment(seed.ParcelId, seed.CourierA);
        await using (var context = Context())
        {
            context.Assignments.Add(assignment);
            await context.SaveChangesAsync();
            switch (status)
            {
                case AssignmentStatus.Delivered:
                    assignment.MarkPickedUp(Now.AddMinutes(1));
                    assignment.MarkDelivered(Now.AddMinutes(2));
                    break;
                case AssignmentStatus.Failed: assignment.MarkFailed(); break;
                case AssignmentStatus.Cancelled: assignment.MarkCancelled(); break;
            }

            await context.SaveChangesAsync();
        }

        await using var verify = Context();
        Assert.False(await verify.Assignments.AnyAsync(x => x.ParcelId == seed.ParcelId && x.Status == AssignmentStatus.Active));
        var saved = await verify.Assignments.SingleAsync(x => x.Id == assignment.Id);
        Assert.Equal(status, saved.Status);
        Assert.Equal(seed.CourierA, saved.CourierId);
        Assert.Equal(assignment.PickedUpAt, saved.PickedUpAt);
        Assert.Equal(assignment.DeliveredAt, saved.DeliveredAt);
    }

    [Fact]
    public async Task DatabaseRejectsInvalidLifecycleValuesAndTimestamps()
    {
        var seed = await SeedAsync();
        await using var context = Context();
        var assignment = NewAssignment(seed.ParcelId, seed.CourierA);
        context.Assignments.Add(assignment);
        await context.SaveChangesAsync();

        await CheckRejectedAsync(context, $"UPDATE assignments SET status = {"Unknown"} WHERE id = {assignment.Id}");
        await CheckRejectedAsync(context, $"UPDATE assignments SET picked_up_at = {Now.AddSeconds(-1)} WHERE id = {assignment.Id}");
        await CheckRejectedAsync(context, $"UPDATE assignments SET status = 'Delivered' WHERE id = {assignment.Id}");
        await CheckRejectedAsync(context, $"UPDATE assignments SET delivered_at = {Now} WHERE id = {assignment.Id}");
        await CheckRejectedAsync(context, $"UPDATE assignments SET status = 'Delivered', picked_up_at = {Now.AddMinutes(2)}, delivered_at = {Now.AddMinutes(1)} WHERE id = {assignment.Id}");
        assignment.MarkPickedUp(Now.AddMinutes(1));
        await context.SaveChangesAsync();
        await CheckRejectedAsync(context, $"UPDATE assignments SET status = 'Reassigned' WHERE id = {assignment.Id}");
        await CheckRejectedAsync(context, $"UPDATE assignments SET status = 'Cancelled' WHERE id = {assignment.Id}");
        await CheckRejectedAsync(context, $"UPDATE assignments SET picked_up_at = NULL WHERE id = {assignment.Id}");
    }

    [Fact]
    public async Task DatabaseRetainsRowsAndPreventsOwnershipRewritesOrClosedResurrection()
    {
        var seed = await SeedAsync();
        await using var context = Context();
        var assignment = NewAssignment(seed.ParcelId, seed.CourierA);
        context.Assignments.Add(assignment);
        await context.SaveChangesAsync();

        await CheckRejectedAsync(context, $"UPDATE assignments SET courier_id = {seed.CourierB} WHERE id = {assignment.Id}");
        await CheckRejectedAsync(context, $"UPDATE assignments SET service_date = {ServiceDate.AddDays(1)} WHERE id = {assignment.Id}");
        await CheckRejectedAsync(context, $"UPDATE assignments SET assigned_at = {Now.AddMinutes(1)} WHERE id = {assignment.Id}");
        await CheckRejectedAsync(context, $"DELETE FROM assignments WHERE id = {assignment.Id}");
        assignment.MarkReassigned();
        await context.SaveChangesAsync();
        await CheckRejectedAsync(context, $"UPDATE assignments SET status = 'Active' WHERE id = {assignment.Id}");
        await CheckRejectedAsync(context, $"DELETE FROM assignments WHERE id = {assignment.Id}");
        context.ChangeTracker.Clear();
        Assert.Equal(AssignmentStatus.Reassigned, (await context.Assignments.SingleAsync(x => x.Id == assignment.Id)).Status);
        var deleteCourier = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM couriers WHERE id = {seed.CourierA}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, deleteCourier.SqlState);
    }

    [Fact]
    public async Task ConcurrentAssignmentInsertsHaveOnlyOneActiveWinner()
    {
        var seed = await SeedAsync();
        await using var first = Context();
        await using var second = Context();
        first.Assignments.Add(NewAssignment(seed.ParcelId, seed.CourierA));
        second.Assignments.Add(NewAssignment(seed.ParcelId, seed.CourierB));
        var outcomes = await RaceAsync(first, second);
        Assert.Single(outcomes, x => x is null);
        AssertState(Assert.IsType<DbUpdateException>(Assert.Single(outcomes, x => x is not null)), PostgresErrorCodes.UniqueViolation);
        await using var verify = Context();
        Assert.Equal(1, await verify.Assignments.CountAsync(x => x.ParcelId == seed.ParcelId && x.Status == AssignmentStatus.Active));
    }

    [Fact]
    public async Task PickupAndReleaseUsingTheSameLoadedAssignmentCannotBothCommit()
    {
        var seed = await SeedAsync();
        var assignment = NewAssignment(seed.ParcelId, seed.CourierA);
        await using (var context = Context())
        {
            context.Assignments.Add(assignment);
            await context.SaveChangesAsync();
        }

        await using var first = Context();
        await using var second = Context();
        var pickup = await first.Assignments.SingleAsync(x => x.Id == assignment.Id);
        var release = await second.Assignments.SingleAsync(x => x.Id == assignment.Id);
        pickup.MarkPickedUp(Now.AddMinutes(1));
        release.MarkReassigned();
        var outcomes = await RaceAsync(first, second);
        Assert.Single(outcomes, x => x is null);
        Assert.IsType<DbUpdateConcurrencyException>(Assert.Single(outcomes, x => x is not null));
        await using var verify = Context();
        var saved = await verify.Assignments.SingleAsync(x => x.Id == assignment.Id);
        Assert.Equal(saved.Status == AssignmentStatus.Active, saved.PickedUpAt is not null);
        Assert.Contains(saved.Status, new[] { AssignmentStatus.Active, AssignmentStatus.Reassigned });
    }

    [Fact]
    public async Task ReplacementFailureRollsBackReleaseAndRetainsOriginalCurrentOwner()
    {
        var seed = await SeedAsync();
        var assignment = NewAssignment(seed.ParcelId, seed.CourierA);
        await using (var context = Context())
        {
            context.Assignments.Add(assignment);
            await context.SaveChangesAsync();
        }

        await using (var context = Context())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var original = await context.Assignments.SingleAsync(x => x.Id == assignment.Id);
            original.MarkReassigned();
            // Flush the release before inserting the replacement inside the same transaction.
            await context.SaveChangesAsync();
            context.Assignments.Add(NewAssignment(seed.ParcelId, Guid.NewGuid()));
            AssertState(await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync()), PostgresErrorCodes.ForeignKeyViolation);
            await transaction.RollbackAsync();
        }

        await using var verify = Context();
        var current = await verify.Assignments.SingleAsync(x => x.ParcelId == seed.ParcelId && x.Status == AssignmentStatus.Active);
        Assert.Equal(assignment.Id, current.Id);
        Assert.Equal(seed.CourierA, current.CourierId);
        Assert.Equal(1, await verify.Assignments.CountAsync(x => x.ParcelId == seed.ParcelId));
        Assert.Single(await verify.BookingHistory.Where(x => x.ParcelId == seed.ParcelId).ToListAsync());
        Assert.NotNull(await verify.OutboxMessages.SingleOrDefaultAsync(x => x.Id == seed.OutboxId));
    }

    [Fact]
    public async Task AssignmentMigrationPreservesEarlierCourierProofAndBookingRecords()
    {
        var schema = "assignment_upgrade_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = schema }.ConnectionString;
            await using var context = Context(scoped);
            await context.GetService<IMigrator>().MigrateAsync("20261028110532_CourierPersistenceFoundation");
            var seed = await SeedAsync(context);
            context.ProofMetadata.Add(new ProofMetadata(Guid.NewGuid(), seed.ParcelId, ProofType.Photo,
                "proofs/" + Guid.NewGuid().ToString("N"), "Fictional Recipient", seed.CourierA, Now));
            await context.SaveChangesAsync();
            await context.Database.MigrateAsync();
            context.Assignments.Add(NewAssignment(seed.ParcelId, seed.CourierA));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            Assert.Equal(2, await context.Couriers.CountAsync());
            Assert.Single(await context.ProofMetadata.ToListAsync());
            Assert.Single(await context.Parcels.ToListAsync());
            Assert.Single(await context.PriceSnapshots.ToListAsync());
            Assert.Single(await context.BookingHistory.ToListAsync());
            Assert.Single(await context.OutboxMessages.ToListAsync());
            Assert.Single(await context.Assignments.ToListAsync());
            Assert.False(context.Database.HasPendingModelChanges());
        }
        finally
        {
            // The schema name is generated here, never a configured/shared schema.
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task BothPhotoAndSignatureMetadataCanBelongToTheSameParcel()
    {
        var seed = await SeedAsync();
        await using var context = Context();
        foreach (var type in new[] { ProofType.Photo, ProofType.Signature })
        {
            context.ProofMetadata.Add(new ProofMetadata(Guid.NewGuid(), seed.ParcelId, type,
                "proofs/" + Guid.NewGuid().ToString("N"), "Fictional Recipient", seed.CourierA, Now));
        }

        await context.SaveChangesAsync();
        Assert.Equal(2, await context.ProofMetadata.CountAsync(x => x.ParcelId == seed.ParcelId));
        Assert.Equal(ParcelStatus.Created, (await context.Parcels.SingleAsync(x => x.Id == seed.ParcelId)).Status);
    }

    private DeliveryDbContext Context(string? connection = null) =>
        new(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(connection ?? fixture.ConnectionString).Options);

    private static CourierAssignment NewAssignment(Guid parcelId, Guid courierId) =>
        new(Guid.NewGuid(), parcelId, courierId, ServiceDate, Now);

    private async Task<Seed> SeedAsync()
    {
        await using var context = Context();
        return await SeedAsync(context);
    }

    private static async Task<Seed> SeedAsync(DeliveryDbContext context)
    {
        // Persistence fixtures do not simulate an authorized dispatcher/status handler.
        var zone = new DeliveryZone(Guid.NewGuid(), "Assignments " + Guid.NewGuid().ToString("N"));
        var tier = new PricingTier(Guid.NewGuid(), zone.Id, ParcelSize.Small, "assignment-test-v1", 450m, 60m);
        var parcel = Parcel.ForCustomer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"),
            new ParcelDetails(ParcelSize.Small, 2.5m, zone.Id, "Fictional pickup", null, "Fictional destination", "Fictional contact"), Now);
        var first = new Api.Domain.Courier(Guid.NewGuid(), true, true, 10);
        var second = new Api.Domain.Courier(Guid.NewGuid(), true, true, 10);
        var outbox = OutboxMessage.ParcelCreated(Guid.NewGuid(), parcel, "assignment-persistence-test");
        context.AddRange(zone, tier, parcel, first, second,
            new PriceSnapshot(Guid.NewGuid(), parcel.Id, tier, 450m, 90m, 540m, Now),
            BookingHistory.Created(Guid.NewGuid(), parcel, parcel.CustomerId!.Value), outbox);
        await context.SaveChangesAsync();
        return new Seed(parcel.Id, first.Id, second.Id, outbox.Id);
    }

    private static async Task CheckRejectedAsync(DeliveryDbContext context, FormattableString sql)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(sql));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    private static void AssertState(DbUpdateException error, string state) =>
        Assert.Equal(state, Assert.IsType<PostgresException>(error.InnerException).SqlState);

    private static async Task<Exception?[]> RaceAsync(DeliveryDbContext first, DeliveryDbContext second)
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
        return await Task.WhenAll(a, b);
    }

    private sealed record Seed(Guid ParcelId, Guid CourierA, Guid CourierB, Guid OutboxId);
}
