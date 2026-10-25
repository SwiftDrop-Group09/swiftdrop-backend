using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Npgsql;

using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Bookings;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.MerchantCompatibility;

[Collection("Postgres")]
public sealed class MerchantBookingPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AuditActor = Guid.Parse("b697ce15-dcae-497e-a4e8-be654e2ea4a3");

    public async Task InitializeAsync()
    {
        await using var database = Context();
        await database.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task WriterWithoutAnExplicitTransactionDoesNotTrackOrPersistBookingRows()
    {
        var tier = await SeedAsync();
        var write = Create(tier, Guid.NewGuid(), "not-submitted");
        await using var database = Context();

        await Assert.ThrowsAsync<InvalidOperationException>(() => BookingWritePersistence.SaveAsync(database, write));

        Assert.Empty(database.ChangeTracker.Entries());
        await AssertAbsentAsync(write);
    }

    [Fact]
    public async Task WriterFlushesAllRecordsButOnlyTheOwningTransactionCanCommitThem()
    {
        var tier = await SeedAsync();
        var write = Create(tier, Guid.NewGuid(), "explicit-commit");
        await using var database = Context();
        await using var transaction = await database.Database.BeginTransactionAsync();

        await BookingWritePersistence.SaveAsync(database, write);

        Assert.NotNull(database.Database.CurrentTransaction);
        Assert.True(await database.Parcels.AnyAsync(item => item.Id == write.Parcel.Id));
        await AssertAbsentAsync(write);
        await transaction.CommitAsync();
        await AssertCommittedAsync(write);
    }

    [Fact]
    public async Task AcceptedMerchantPriceRemainsImmutableAfterCommit()
    {
        var tier = await SeedAsync();
        var write = Create(tier, Guid.NewGuid(), "immutable-price");
        await PersistAsync(write);

        await using (var database = Context())
        {
            var snapshot = await database.PriceSnapshots.SingleAsync(item => item.Id == write.Snapshot.Id);
            database.Entry(snapshot).Property(item => item.Total).CurrentValue = 999m;
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }

        await using (var database = Context())
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => database.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE price_snapshots SET base_fee = 500, total = 590 WHERE id = {write.Snapshot.Id}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        }

        await AssertCommittedAsync(write);
    }

    [Fact]
    public async Task OwningTransactionRollbackRemovesParcelSnapshotHistoryAndOutboxTogether()
    {
        var tier = await SeedAsync();
        var write = Create(tier, Guid.NewGuid(), "rolled-back");
        await using (var database = Context())
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            await BookingWritePersistence.SaveAsync(database, write);
            await transaction.RollbackAsync();
        }

        await AssertAbsentAsync(write);
    }

    [Fact]
    public async Task MerchantOrderConstraintRejectsAnotherCreationWithoutExtraBookingRows()
    {
        var tier = await SeedAsync();
        var merchantId = Guid.NewGuid();
        var original = Create(tier, merchantId, "stable-external-order");
        var duplicate = Create(tier, merchantId, "stable-external-order");
        Assert.NotEqual(original.Parcel.TrackingNumber, duplicate.Parcel.TrackingNumber);
        await PersistAsync(original);

        await using (var database = Context())
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => BookingWritePersistence.SaveAsync(database, duplicate));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
            await transaction.RollbackAsync();
        }

        await AssertCommittedAsync(original);
        await AssertAbsentAsync(duplicate);
        await using var verification = Context();
        Assert.Equal(1, await verification.Parcels.CountAsync(item => item.MerchantId == merchantId));
    }

    [Fact]
    public async Task TrackingCollisionRollsBackTheEntireSecondMerchantWrite()
    {
        var tier = await SeedAsync();
        var merchantId = Guid.NewGuid();
        var original = Create(tier, merchantId, "original-order");
        var collision = Create(tier, merchantId, "different-order", original.Parcel.TrackingNumber);
        await PersistAsync(original);

        await using (var database = Context())
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => BookingWritePersistence.SaveAsync(database, collision));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
            Assert.Equal(DeliveryDatabaseFailure.DuplicateTrackingNumber, DeliveryDatabaseErrors.Classify(failure));
            await transaction.RollbackAsync();
        }

        await AssertCommittedAsync(original);
        await AssertAbsentAsync(collision);
    }

    [Fact]
    public async Task DifferentMerchantsCanUseTheSameExternalOrderIdentifier()
    {
        var tier = await SeedAsync();
        var first = Create(tier, Guid.NewGuid(), "order-used-by-two-businesses");
        var second = Create(tier, Guid.NewGuid(), "order-used-by-two-businesses");
        await using (var database = Context())
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            await BookingWritePersistence.SaveAsync(database, first);
            await BookingWritePersistence.SaveAsync(database, second);
            await transaction.CommitAsync();
        }

        await AssertCommittedAsync(first);
        await AssertCommittedAsync(second);
        Assert.NotEqual(first.Parcel.MerchantId, second.Parcel.MerchantId);
    }

    private DeliveryDbContext Context() => new(new DbContextOptionsBuilder<DeliveryDbContext>()
        .UseNpgsql(fixture.ConnectionString).Options);

    private async Task<PricingTier> SeedAsync()
    {
        var zone = new DeliveryZone(Guid.NewGuid(), "Merchant compatibility " + Guid.NewGuid().ToString("N"));
        var tier = new PricingTier(Guid.NewGuid(), zone.Id, ParcelSize.Small, "test-merchant-v1", 450m, 60m);
        await using var database = Context();
        database.Zones.Add(zone);
        database.PricingTiers.Add(tier);
        await database.SaveChangesAsync();
        return tier;
    }

    private static BookingWriteSet Create(PricingTier tier, Guid merchantId, string order, string? tracking = null)
    {
        var details = new ParcelDetails(ParcelSize.Small, 2.5m, tier.ZoneId,
            "Fictional pickup", null, "Fictional destination", "Fictional Recipient / 0000000000");
        Assert.True(PriceCalculator.TryCalculate(tier.BaseRate, tier.PerKgRate, details.WeightKg, out var price));
        return BookingAssembler.CreateMerchant(merchantId, order, tier.PricingVersion, details, tier,
            price, AuditActor, new Tracking(tracking ?? "TRK-" + Guid.NewGuid().ToString("N")), Now, "merchant-compatibility-test");
    }

    private async Task PersistAsync(BookingWriteSet write)
    {
        await using var database = Context();
        await using var transaction = await database.Database.BeginTransactionAsync();
        await BookingWritePersistence.SaveAsync(database, write);
        await transaction.CommitAsync();
    }

    private async Task AssertAbsentAsync(BookingWriteSet write)
    {
        await using var database = Context();
        Assert.False(await database.Parcels.AnyAsync(item => item.Id == write.Parcel.Id));
        Assert.False(await database.PriceSnapshots.AnyAsync(item => item.ParcelId == write.Parcel.Id));
        Assert.False(await database.BookingHistory.AnyAsync(item => item.ParcelId == write.Parcel.Id));
        Assert.False(await database.OutboxMessages.AnyAsync(item => item.Id == write.Outbox.Id));
    }

    private async Task AssertCommittedAsync(BookingWriteSet write)
    {
        await using var database = Context();
        var parcel = await database.Parcels.SingleAsync(item => item.Id == write.Parcel.Id);
        Assert.Null(parcel.CustomerId);
        Assert.Equal(write.Parcel.MerchantId, parcel.MerchantId);
        Assert.Equal(write.Parcel.ExternalOrderId, parcel.ExternalOrderId);
        Assert.Equal(write.Parcel.TrackingNumber, parcel.TrackingNumber);
        Assert.Equal(ParcelStatus.Created, parcel.Status);
        Assert.Equal(1, parcel.Version);
        Assert.Equal(Now, parcel.CreatedAt);
        Assert.Equal(Now, parcel.UpdatedAt);
        var snapshot = await database.PriceSnapshots.SingleAsync(item => item.ParcelId == parcel.Id);
        Assert.Equal(write.Snapshot.Id, snapshot.Id);
        Assert.Equal(540m, snapshot.Total);
        Assert.Equal("LKR", snapshot.Currency);
        Assert.Equal("test-merchant-v1", snapshot.PricingVersion);
        var history = await database.BookingHistory.SingleAsync(item => item.ParcelId == parcel.Id);
        Assert.Equal(AuditActor, history.ChangedBy);
        Assert.Null(history.FromStatus);
        Assert.Equal(ParcelStatus.Created, history.ToStatus);
        var outbox = await database.OutboxMessages.SingleAsync(item => item.Id == write.Outbox.Id);
        Assert.Null(outbox.PublishedAt);
        using var payload = JsonDocument.Parse(outbox.Payload);
        Assert.Equal(parcel.Id.ToString("D"), payload.RootElement.GetProperty("parcelId").GetString());
        Assert.Equal(parcel.MerchantId!.Value.ToString("D"), payload.RootElement.GetProperty("merchantId").GetString());
        Assert.Equal("Created", payload.RootElement.GetProperty("status").GetString());
    }

    private sealed class Tracking(string value) : ITrackingNumberGenerator
    {
        public string Generate() => value;
    }
}
