using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.CourierStatus;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Authentication;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierStatusMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task UpgradePreservesRealBookingReplayAndRetainsOperationScoped200Results()
    {
        var schema = "status_upgrade_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = schema }.ConnectionString;
            DeliveryDbContext Context() => new(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(connection).Options);
            await using var context = Context();
            await context.GetService<IMigrator>().MigrateAsync("20261028112000_AssignmentsPersistence");
            var zone = new DeliveryZone(Guid.NewGuid(), "Migration status zone");
            context.AddRange(zone, new PricingTier(Guid.NewGuid(), zone.Id, ParcelSize.Small, "migration-v1", 450m, 60m));
            await context.SaveChangesAsync();
            var owner = Guid.NewGuid();
            using var factory = new QuoteApiFactory(connection, DeliveryTestAuthentication.Customer(owner));
            using var client = factory.CreateClient();
            using var quote = await client.PostAsJsonAsync("/api/v1/quotes", new QuoteInput(
                new AddressInput("Fictional pickup", "Colombo", "00100", zone.Id.ToString()),
                new AddressInput("Fictional delivery", "Colombo", "00100", zone.Id.ToString()),
                new PackageDetailsInput(2.5m, "Small"), new RecipientInput("Fictional Recipient", "0000000000")));
            Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
            using var estimated = JsonDocument.Parse(await quote.Content.ReadAsStringAsync());
            var confirm = new ConfirmInput(estimated.RootElement.GetProperty("quoteId").GetString(), 540m);
            var bookingKey = Guid.NewGuid().ToString();
            async Task<HttpResponseMessage> BookAsync()
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/parcels") { Content = JsonContent.Create(confirm) };
                request.Headers.Add("Idempotency-Key", bookingKey);
                return await client.SendAsync(request);
            }
            using var created = await BookAsync();
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var originalBody = await created.Content.ReadAsStringAsync();
            using var booked = JsonDocument.Parse(originalBody);
            var parcelId = booked.RootElement.GetProperty("id").GetGuid();
            var bookingRecord = await context.IdempotencyRecords.AsNoTracking().SingleAsync();
            var originalEnvelope = bookingRecord.StoredResponse;
            await context.Database.MigrateAsync();
            Assert.Equal(originalEnvelope, (await context.IdempotencyRecords.AsNoTracking().SingleAsync()).StoredResponse);

            // Test-only dispatcher snapshot, using the same approved fields/audit fixture.
            var courierId = Guid.NewGuid();
            context.Couriers.Add(new Api.Domain.Courier(courierId, true, true, 10));
            context.CourierZones.Add(new CourierZone(courierId, zone.Id));
            var parcel = await context.Parcels.SingleAsync(x => x.Id == parcelId);
            context.Entry(parcel).Property(x => x.Status).CurrentValue = ParcelStatus.Assigned;
            context.Entry(parcel).Property(x => x.Version).CurrentValue = 2;
            context.Assignments.Add(new CourierAssignment(Guid.NewGuid(), parcelId, courierId,
                new(2026, 10, 23), QuoteApiFactory.Now));
            StatusTestScenario.AddDispatchAudit(context, parcel, ParcelStatus.Created, QuoteApiFactory.Now);
            await context.SaveChangesAsync();
            using var courierFactory = new QuoteApiFactory(connection, DeliveryTestAuthentication.Principal(courierId.ToString(), "Courier"));
            using var courier = courierFactory.CreateClient();
            using var updated = await CourierStatusAuthenticationTests.Send(courier,
                "{\"status\":\"PickedUp\",\"expectedVersion\":2,\"notes\":\"migration private note\"}",
                Guid.NewGuid().ToString(), $"/api/v1/deliveries/{parcelId}/status");
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var replay = await BookAsync();
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync());
            Assert.Equal(2, await context.IdempotencyRecords.CountAsync());
            var statusRecord = await context.IdempotencyRecords.AsNoTracking().SingleAsync(x => x.Operation == CourierStatusValidator.Operation);
            using var envelope = JsonDocument.Parse(statusRecord.StoredResponse!);
            Assert.Equal(200, envelope.RootElement.GetProperty("statusCode").GetInt32());
            Assert.False(context.Database.HasPendingModelChanges());

            var invalid = new IdempotencyRecord(Guid.NewGuid(), owner.ToString(), CourierStatusValidator.Operation,
                Guid.NewGuid().ToString(), "fingerprint", QuoteApiFactory.Now);
            invalid.CompleteStatusUpdate(parcelId, "{}");
            context.IdempotencyRecords.Add(invalid);
            context.Entry(invalid).Property(x => x.Operation).CurrentValue = "NotAStatusOperation";
            var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(rejected.InnerException).SqlState);
            context.ChangeTracker.Clear();
            var immutable = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE idempotency_keys SET stored_response = NULL, parcel_id = NULL WHERE id = {statusRecord.Id}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, immutable.SqlState);
            // Downgrade cannot discard/transform retained 200 results to restore the old constraint.
            var downgrade = await Assert.ThrowsAsync<PostgresException>(() => context.GetService<IMigrator>()
                .MigrateAsync("20261028112000_AssignmentsPersistence"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, downgrade.SqlState);
            Assert.Equal(statusRecord.StoredResponse, (await context.IdempotencyRecords.AsNoTracking()
                .SingleAsync(x => x.Id == statusRecord.Id)).StoredResponse);
        }
        finally
        {
            // The name is generated in this test, inside the fixture-owned database.
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
