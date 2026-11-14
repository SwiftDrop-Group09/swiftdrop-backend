using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Application.CourierStatus;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierProofMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task UpgradeRetainsStatusReceiptsAddsProof200AndProtectsCommittedProofFromMutationAndUnsafeDowngrade()
    {
        var schema = "proof_upgrade_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = schema }.ConnectionString;
            await using var context = new DeliveryDbContext(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(connection).Options);
            await context.GetService<IMigrator>().MigrateAsync("20261028170439_CourierStatusIdempotency");
            var zone = new DeliveryZone(Guid.NewGuid(), "Proof migration zone");
            var tier = new PricingTier(Guid.NewGuid(), zone.Id, ParcelSize.Small, "proof-migration-v1", 450m, 60m);
            var actor = Guid.NewGuid();
            var owner = Guid.NewGuid();
            var createdAt = QuoteApiFactory.Now.AddHours(-1);
            var parcel = Parcel.ForCustomer(Guid.NewGuid(), owner, Guid.NewGuid().ToString("N"),
                new(ParcelSize.Small, 2.5m, zone.Id, "private pickup", null, "private dropoff", "private recipient"), createdAt);
            context.AddRange(zone, tier, parcel, new PriceSnapshot(Guid.NewGuid(), parcel.Id, tier, 450m, 90m, 540m, createdAt),
                new Api.Domain.Courier(actor, true, true, 10), new CourierZone(actor, zone.Id),
                BookingHistory.Created(Guid.NewGuid(), parcel, owner), OutboxMessage.ParcelCreated(Guid.NewGuid(), parcel, "proof-migration"));
            await context.SaveChangesAsync();
            context.Entry(parcel).Property(x => x.Status).CurrentValue = ParcelStatus.Assigned;
            context.Entry(parcel).Property(x => x.Version).CurrentValue = 2;
            context.Assignments.Add(new CourierAssignment(Guid.NewGuid(), parcel.Id, actor, new(2026, 10, 23), createdAt.AddMinutes(1)));
            StatusTestScenario.AddDispatchAudit(context, parcel, ParcelStatus.Created, createdAt.AddMinutes(1));
            await context.SaveChangesAsync();
            var storage = new MemoryProofStorage { Now = QuoteApiFactory.Now };
            using var factory = new QuoteApiFactory(connection, QuoteApiFactory.Principal(actor.ToString(), "Courier"),
                configureServices: services => ProofTestSupport.UseStorage(services, storage));
            using var client = factory.CreateClient();
            string? firstBody = null;
            var version = 2;
            foreach (var status in new[] { "PickedUp", "InTransit", "OutForDelivery" })
            {
                using var response = await CourierStatusAuthenticationTests.Send(client,
                    $"{{\"status\":\"{status}\",\"expectedVersion\":{version},\"notes\":\"migration private note\"}}",
                    "migration-" + status, $"/api/v1/deliveries/{parcel.Id}/status");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                if (firstBody is null) firstBody = await response.Content.ReadAsStringAsync();
                version++;
            }
            var originals = await context.IdempotencyRecords.AsNoTracking().OrderBy(x => x.Key).Select(x => x.StoredResponse).ToArrayAsync();
            await context.Database.MigrateAsync();
            Assert.Equal(originals, await context.IdempotencyRecords.AsNoTracking().OrderBy(x => x.Key).Select(x => x.StoredResponse).ToArrayAsync());
            using var completed = await ProofTestSupport.Send(client, parcel.Id, ProofTestSupport.Form());
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            using var statusReplay = await CourierStatusAuthenticationTests.Send(client,
                "{\"status\":\"PickedUp\",\"expectedVersion\":2,\"notes\":\"migration private note\"}",
                "migration-PickedUp", $"/api/v1/deliveries/{parcel.Id}/status");
            Assert.Equal(HttpStatusCode.OK, statusReplay.StatusCode);
            Assert.Equal(firstBody, await statusReplay.Content.ReadAsStringAsync());
            var proof = await context.ProofMetadata.SingleAsync(x => x.ParcelId == parcel.Id);
            context.ProofMetadata.Remove(proof);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
            context.ChangeTracker.Clear();
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM proof_metadata WHERE id = {proof.Id}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
            var downgrade = await Assert.ThrowsAsync<PostgresException>(() => context.GetService<IMigrator>()
                .MigrateAsync("20261028170439_CourierStatusIdempotency"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, downgrade.SqlState);
            Assert.Equal(1, await context.ProofMetadata.CountAsync(x => x.ParcelId == parcel.Id));
            Assert.Equal(1, await context.IdempotencyRecords.CountAsync(x => x.Operation == ProofValidator.Operation));
            Assert.Equal(3, await context.IdempotencyRecords.CountAsync(x => x.Operation == CourierStatusValidator.Operation));
            Assert.False(context.Database.HasPendingModelChanges());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
