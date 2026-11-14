using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Courier;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Dispatch;

[Collection("Postgres")]
public sealed class AssignmentMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task NarrowUpgradePreservesRetainedResponsesAndRejectsUnrelated200WhileDownCannotDiscardAssignments()
    {
        var schema = "assignment_upgrade_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            // Clone only seeded parcel data into an independently migrated fixture schema.
            await using var seed = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
            var graph = await seed.GraphAsync();
            var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = schema }.ConnectionString;
            await using var db = new DeliveryDbContext(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(connection).Options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261113010000_ActorActivityAudit");
            db.Zones.Add(new DeliveryZone(graph.Parcel.ZoneId, "Migration fixture"));
            db.Parcels.Add(graph.Parcel);
            await using var source = seed.Context();
            db.PricingTiers.Add(await source.PricingTiers.AsNoTracking().SingleAsync(x => x.Id == graph.Snapshot.PricingTierId));
            db.PriceSnapshots.Add(graph.Snapshot);
            var records = new[] { "CreateParcel", "UpdateDeliveryStatus", "CompleteDeliveryWithProof" }.Select(operation =>
            {
                var record = new IdempotencyRecord(Guid.NewGuid(), "fixture", operation, operation, "fingerprint", seed.Clock.UtcNow);
                if (operation == "CreateParcel") record.Complete(graph.Parcel.Id, 201, "{}", "/fixture");
                else if (operation == "UpdateDeliveryStatus") record.CompleteStatusUpdate(graph.Parcel.Id, "{}");
                else record.CompleteDeliveryWithProof(graph.Parcel.Id, "{}");
                return record;
            }).ToArray();
            db.IdempotencyRecords.AddRange(records);
            await db.SaveChangesAsync();
            var before = await db.IdempotencyRecords.AsNoTracking().OrderBy(x => x.Id).Select(x => x.StoredResponse).ToArrayAsync();
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(before, await db.IdempotencyRecords.AsNoTracking().OrderBy(x => x.Id).Select(x => x.StoredResponse).ToArrayAsync());
            await migrator.MigrateAsync("20261113010000_ActorActivityAudit");
            await db.Database.MigrateAsync();
            var assignment = new IdempotencyRecord(Guid.NewGuid(), "fixture", "AssignCourier", "assign", "fingerprint", seed.Clock.UtcNow);
            assignment.CompleteAssignment(graph.Parcel.Id, "{}");
            db.IdempotencyRecords.Add(assignment);
            await db.SaveChangesAsync();
            var retainedAssignment = await db.IdempotencyRecords.AsNoTracking().Where(x => x.Id == assignment.Id)
                .Select(x => x.StoredResponse).SingleAsync();
            var invalid = new IdempotencyRecord(Guid.NewGuid(), "fixture", "AssignCourier", "invalid", "fingerprint", seed.Clock.UtcNow);
            invalid.CompleteAssignment(graph.Parcel.Id, "{}");
            db.IdempotencyRecords.Add(invalid);
            db.Entry(invalid).Property(x => x.Operation).CurrentValue = "UnrelatedOperation";
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
            db.ChangeTracker.Clear();
            var down = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261113010000_ActorActivityAudit"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, down.SqlState);
            Assert.Equal(retainedAssignment, (await db.IdempotencyRecords.AsNoTracking().SingleAsync(x => x.Id == assignment.Id)).StoredResponse);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
