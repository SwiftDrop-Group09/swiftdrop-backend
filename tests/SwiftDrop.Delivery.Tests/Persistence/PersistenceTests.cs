using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Tests.Persistence;

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }

[Collection("Postgres")]
public sealed class PersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await using var context = Context();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReapplyingMigrationPreservesExistingDataAndMatchesTheCurrentModel()
    {
        var seed = await SeedAsync();
        var parcel = await PersistParcelAsync(seed);
        await using var context = Context();
        var appliedMigrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();

        Assert.NotEmpty(appliedMigrations);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());

        await context.Database.MigrateAsync();

        Assert.Equal(appliedMigrations, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(seed.Zone.Name, (await context.Zones.SingleAsync(item => item.Id == seed.Zone.Id)).Name);
        Assert.Equal(seed.Tier.PricingVersion,
            (await context.PricingTiers.SingleAsync(item => item.Id == seed.Tier.Id)).PricingVersion);
        var savedQuote = await context.Quotes.SingleAsync(item => item.Id == seed.Quote.Id);
        Assert.Equal(seed.Quote.Total, savedQuote.Total);
        using var originalInput = JsonDocument.Parse(seed.Quote.DeliveryInput);
        using var savedInput = JsonDocument.Parse(savedQuote.DeliveryInput);
        Assert.True(JsonElement.DeepEquals(originalInput.RootElement, savedInput.RootElement));
        Assert.Equal(parcel.TrackingNumber,
            (await context.Parcels.SingleAsync(item => item.Id == parcel.Id)).TrackingNumber);
        Assert.Equal(540m,
            (await context.PriceSnapshots.SingleAsync(item => item.ParcelId == parcel.Id)).Total);
    }

    [Fact]
    public async Task QuotePersistsOwnerCompleteInputAndVersionWithoutCreatingBookingRows()
    {
        await using var before = Context();
        var parcelCount = await before.Parcels.CountAsync();
        var historyCount = await before.BookingHistory.CountAsync();
        var outboxCount = await before.OutboxMessages.CountAsync();
        var seed = await SeedAsync();
        await using var context = Context();

        var quote = await context.Quotes.SingleAsync(item => item.Id == seed.Quote.Id);

        Assert.Equal(seed.OwnerId, quote.OwnerId);
        Assert.Equal(seed.Tier.Id, quote.PricingTierId);
        Assert.Equal(seed.Tier.PricingVersion, quote.PricingVersion);
        Assert.Equal(Now.AddMinutes(10), quote.ExpiresAt);
        Assert.Contains("Fictional Recipient", quote.DeliveryInput);
        Assert.Null(quote.ConsumedByParcelId);
        Assert.Equal(parcelCount, await context.Parcels.CountAsync());
        Assert.Equal(historyCount, await context.BookingHistory.CountAsync());
        Assert.Equal(outboxCount, await context.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task PostgreSqlRejectsDuplicateTrackingNumbers()
    {
        var seed = await SeedAsync();
        var tracking = Guid.NewGuid().ToString("N");
        await PersistParcelAsync(seed, tracking);
        await using var context = Context();
        AddParcel(context, seed, tracking);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        AssertPostgresState(exception, PostgresErrorCodes.UniqueViolation);
        Assert.Equal(DeliveryDatabaseFailure.DuplicateTrackingNumber, DeliveryDatabaseErrors.Classify(exception));
    }

    [Fact]
    public async Task PricingTierMustReferenceAnExistingDeliveryZone()
    {
        await using var context = Context();
        context.PricingTiers.Add(new PricingTier(
            Guid.NewGuid(), Guid.NewGuid(), ParcelSize.Small, "orphan-tier", 450m, 60m));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        AssertPostgresState(exception, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task QuoteMustReferenceAnExistingPricingTier()
    {
        await using var context = Context();
        var missingTier = new PricingTier(Guid.NewGuid(), Guid.NewGuid(), ParcelSize.Small, "missing", 450m, 60m);
        context.Quotes.Add(new Quote(Guid.NewGuid(), Guid.NewGuid(), "fingerprint", "{}", missingTier, 540m, Now));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        AssertPostgresState(exception, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task NewTariffVersionPreservesThePreviouslyQuotedPrice()
    {
        var seed = await SeedAsync();
        await using var context = Context();
        context.PricingTiers.Add(new PricingTier(Guid.NewGuid(), seed.Zone.Id, ParcelSize.Small, "test-v2", 600m, 80m));
        await context.SaveChangesAsync();

        var quote = await context.Quotes.SingleAsync(item => item.Id == seed.Quote.Id);
        Assert.Equal("test-v1", quote.PricingVersion);
        Assert.Equal(540m, quote.Total);
        Assert.Equal(2, await context.PricingTiers.CountAsync(item => item.ZoneId == seed.Zone.Id));
        var rewritten = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE pricing_tiers SET base_rate = 600 WHERE id = {seed.Tier.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, rewritten.SqlState);
    }

    [Theory]
    [InlineData("zeroWeight", "ck_parcels_weight")]
    [InlineData("overweight", "ck_parcels_weight")]
    [InlineData("origin", "ck_parcels_origin")]
    [InlineData("size", "ck_parcels_size")]
    public async Task PostgreSqlRejectsInvalidParcelInsertsEvenWhenDomainGuardsAreBypassed(
        string invalidField, string expectedConstraint)
    {
        var seed = await SeedAsync();
        await using var context = Context();
        var parcel = AddParcel(context, seed);
        switch (invalidField)
        {
            case "zeroWeight":
                context.Entry(parcel).Property(item => item.WeightKg).CurrentValue = 0m;
                break;
            case "overweight":
                context.Entry(parcel).Property(item => item.WeightKg).CurrentValue = 30.0001m;
                break;
            case "origin":
                context.Entry(parcel).Property(item => item.MerchantId).CurrentValue = Guid.NewGuid();
                context.Entry(parcel).Property(item => item.ExternalOrderId).CurrentValue = "external-order";
                break;
            case "size":
                context.Entry(parcel).Property(item => item.Size).CurrentValue = (ParcelSize)99;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidField));
        }

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        AssertPostgresState(exception, PostgresErrorCodes.CheckViolation);
        Assert.Equal(expectedConstraint, Assert.IsType<PostgresException>(exception.InnerException).ConstraintName);
        await using var verification = Context();
        Assert.False(await verification.Parcels.AnyAsync(item => item.Id == parcel.Id));
        Assert.False(await verification.PriceSnapshots.AnyAsync(item => item.ParcelId == parcel.Id));
    }

    [Fact]
    public async Task EveryCommittedParcelRequiresExactlyOneSnapshot()
    {
        var seed = await SeedAsync();
        await using (var missingContext = Context())
        {
            var missing = ParcelFor(seed);
            missingContext.Parcels.Add(missing);

            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => missingContext.SaveChangesAsync());

            AssertPostgresState(exception, PostgresErrorCodes.CheckViolation);
        }

        var parcel = await PersistParcelAsync(seed);
        await using var duplicateContext = Context();
        duplicateContext.PriceSnapshots.Add(Snapshot(seed, parcel));

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync());

        AssertPostgresState(duplicate, PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task SnapshotRequiresAnExistingParcel()
    {
        var seed = await SeedAsync();
        await using var context = Context();
        context.PriceSnapshots.Add(new PriceSnapshot(Guid.NewGuid(), Guid.NewGuid(), seed.Tier, 450m, 90m, 540m, Now));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        AssertPostgresState(exception, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task SnapshotMustMatchItsParcelsDeliveryZone()
    {
        var seed = await SeedAsync();
        var otherZone = await SeedAsync();
        var parcel = ParcelFor(seed);
        var snapshot = Snapshot(otherZone, parcel);
        await using var context = Context();
        context.Parcels.Add(parcel);
        context.PriceSnapshots.Add(snapshot);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        AssertPostgresState(exception, PostgresErrorCodes.ForeignKeyViolation);
        Assert.Equal("FK_price_snapshots_parcels_parcel_id_zone_id",
            Assert.IsType<PostgresException>(exception.InnerException).ConstraintName);
        await using var verification = Context();
        Assert.False(await verification.Parcels.AnyAsync(item => item.Id == parcel.Id));
        Assert.False(await verification.PriceSnapshots.AnyAsync(item => item.Id == snapshot.Id));
    }

    [Fact]
    public async Task AcceptedSnapshotIsImmutableThroughTrackedAndDirectSqlWrites()
    {
        var seed = await SeedAsync();
        var parcel = await PersistParcelAsync(seed);
        await using (var context = Context())
        {
            var snapshot = await context.PriceSnapshots.SingleAsync(item => item.ParcelId == parcel.Id);
            context.Entry(snapshot).Property(item => item.Total).CurrentValue = 999m;

            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        }

        await using (var context = Context())
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE price_snapshots SET base_fee = 500, total = 590 WHERE parcel_id = {parcel.Id}"));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            var deletion = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM price_snapshots WHERE parcel_id = {parcel.Id}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, deletion.SqlState);
        }

        await using var verification = Context();
        var saved = await verification.PriceSnapshots.SingleAsync(item => item.ParcelId == parcel.Id);
        Assert.Equal(450m, saved.BaseFee);
        Assert.Equal(90m, saved.WeightFee);
        Assert.Equal(540m, saved.Total);
    }

    [Fact]
    public async Task DatabaseRejectsConsumingAQuoteForAnotherCustomersParcel()
    {
        var seed = await SeedAsync();
        var foreign = await PersistParcelAsync(seed with { OwnerId = Guid.NewGuid() });
        await using var context = Context();
        var quote = await context.Quotes.SingleAsync(item => item.Id == seed.Quote.Id);
        context.Entry(quote).Property(item => item.ConsumedByParcelId).CurrentValue = foreign.Id;

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        AssertPostgresState(exception, PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task ConsumedQuoteCannotBeRedirectedOrClearedThroughSql()
    {
        var seed = await SeedAsync();
        var first = await PersistParcelAsync(seed);
        var second = await PersistParcelAsync(seed);
        await using (var context = Context())
        {
            var quote = await context.Quotes.SingleAsync(item => item.Id == seed.Quote.Id);
            quote.Consume(first, Now.AddMinutes(1));
            await context.SaveChangesAsync();
        }

        await using var sqlContext = Context();
        var redirect = await Assert.ThrowsAsync<PostgresException>(() => sqlContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE quotes SET consumed_by_parcel = {second.Id} WHERE id = {seed.Quote.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, redirect.SqlState);
        var clear = await Assert.ThrowsAsync<PostgresException>(() => sqlContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE quotes SET consumed_by_parcel = NULL WHERE id = {seed.Quote.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, clear.SqlState);
    }

    [Fact]
    public async Task ParcelOwnerCannotChangeAfterQuoteConsumption()
    {
        var seed = await SeedAsync();
        var parcel = await PersistParcelAsync(seed);
        await using (var context = Context())
        {
            var quote = await context.Quotes.SingleAsync(item => item.Id == seed.Quote.Id);
            quote.Consume(parcel, Now.AddMinutes(1));
            await context.SaveChangesAsync();
        }

        await using var verification = Context();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => verification.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE parcels SET customer_id = {Guid.NewGuid()} WHERE id = {parcel.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(seed.OwnerId, (await verification.Parcels.SingleAsync(item => item.Id == parcel.Id)).CustomerId);
    }

    [Fact]
    public async Task IdempotencyUniquenessIsScopedToPrincipalOperationAndKey()
    {
        var seed = await SeedAsync();
        var parcel = await PersistParcelAsync(seed);
        var key = Guid.NewGuid().ToString("N");
        await using (var context = Context())
        {
            context.IdempotencyRecords.AddRange(
                CompletedRecord(parcel, "principal-a", "CreateParcel", key),
                CompletedRecord(parcel, "principal-b", "CreateParcel", key),
                CompletedRecord(parcel, "principal-a", "OtherOperation", key));
            await context.SaveChangesAsync();
        }

        await using var duplicateContext = Context();
        duplicateContext.IdempotencyRecords.Add(CompletedRecord(parcel, "principal-a", "CreateParcel", key, "changed-payload"));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync());

        AssertPostgresState(exception, PostgresErrorCodes.UniqueViolation);
        Assert.Equal(DeliveryDatabaseFailure.DuplicateIdempotencyKey, DeliveryDatabaseErrors.Classify(exception));
        await using var verification = Context();
        Assert.Equal(3, await verification.IdempotencyRecords.CountAsync(item => item.Key == key));
    }

    [Fact]
    public async Task DurableIdempotencyResponsePreservesExactBodyAndCannotBeChangedOrDeleted()
    {
        var seed = await SeedAsync();
        var parcel = await PersistParcelAsync(seed);
        const string body = "{ \"total\": 540.00, \"message\": \"fictional result\" }";
        var record = new IdempotencyRecord(Guid.NewGuid(), seed.OwnerId.ToString(), "CreateParcel", "exact-body", "fingerprint", Now);
        record.Complete(parcel.Id, 201, body, "/api/v1/parcels/" + parcel.Id);
        await using (var context = Context())
        {
            context.IdempotencyRecords.Add(record);
            await context.SaveChangesAsync();
        }

        await using var verification = Context();
        var saved = await verification.IdempotencyRecords.SingleAsync(item => item.Id == record.Id);
        using var response = JsonDocument.Parse(saved.StoredResponse!);
        Assert.Equal(body, response.RootElement.GetProperty("body").GetString());
        Assert.Equal(201, response.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal("/api/v1/parcels/" + parcel.Id, response.RootElement.GetProperty("location").GetString());
        Assert.Equal(Now.AddDays(30), saved.ExpiresAt);
        var changed = await Assert.ThrowsAsync<PostgresException>(() => verification.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE idempotency_keys SET request_fingerprint = {"changed"} WHERE id = {record.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, changed.SqlState);
        var deleted = await Assert.ThrowsAsync<PostgresException>(() => verification.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM idempotency_keys WHERE id = {record.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, deleted.SqlState);
    }

    [Fact]
    public async Task MerchantExternalOrderIdentityRemainsUniqueRegardlessOfBookingKey()
    {
        var seed = await SeedAsync();
        var merchant = Guid.NewGuid();
        var externalOrder = Guid.NewGuid().ToString("N");
        await using (var context = Context())
        {
            var parcel = Parcel.ForMerchant(Guid.NewGuid(), merchant, externalOrder, Guid.NewGuid().ToString("N"), Details(seed), Now);
            context.Parcels.Add(parcel);
            context.PriceSnapshots.Add(Snapshot(seed, parcel));
            await context.SaveChangesAsync();
        }

        await using var duplicateContext = Context();
        var duplicate = Parcel.ForMerchant(Guid.NewGuid(), merchant, externalOrder, Guid.NewGuid().ToString("N"), Details(seed), Now);
        duplicateContext.Parcels.Add(duplicate);
        duplicateContext.PriceSnapshots.Add(Snapshot(seed, duplicate));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync());

        AssertPostgresState(exception, PostgresErrorCodes.UniqueViolation);
        Assert.Equal(DeliveryDatabaseFailure.DuplicateExternalOrder, DeliveryDatabaseErrors.Classify(exception));
    }

    [Fact]
    public async Task DatabaseFailureRollsBackAllBookingRowsAndRestoresUnconsumedQuote()
    {
        var seed = await SeedAsync();
        var tracking = Guid.NewGuid().ToString("N");
        Guid parcelId;
        Guid eventId;
        Guid recordId;
        await using (var context = Context())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var parcel = AddParcel(context, seed, tracking);
            parcelId = parcel.Id;
            var quote = await context.Quotes.SingleAsync(item => item.Id == seed.Quote.Id);
            quote.Consume(parcel, Now.AddMinutes(1));
            context.BookingHistory.Add(BookingHistory.Created(Guid.NewGuid(), parcel, seed.OwnerId));
            var outbox = OutboxMessage.ParcelCreated(Guid.NewGuid(), parcel, "rollback-test");
            eventId = outbox.Id;
            context.OutboxMessages.Add(outbox);
            var record = CompletedRecord(parcel, seed.OwnerId.ToString(), "CreateParcel", Guid.NewGuid().ToString("N"));
            recordId = record.Id;
            context.IdempotencyRecords.Add(record);
            await context.SaveChangesAsync();
            Assert.True(await context.Parcels.AnyAsync(item => item.Id == parcelId));

            var failure = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE parcels SET weight_kg = 0 WHERE id = {parcel.Id}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
            await transaction.RollbackAsync();
        }

        await using var verification = Context();
        Assert.False(await verification.Parcels.AnyAsync(item => item.Id == parcelId));
        Assert.False(await verification.PriceSnapshots.AnyAsync(item => item.ParcelId == parcelId));
        Assert.False(await verification.BookingHistory.AnyAsync(item => item.ParcelId == parcelId));
        Assert.False(await verification.OutboxMessages.AnyAsync(item => item.Id == eventId));
        Assert.False(await verification.IdempotencyRecords.AnyAsync(item => item.Id == recordId));
        Assert.Null((await verification.Quotes.SingleAsync(item => item.Id == seed.Quote.Id)).ConsumedByParcelId);
    }

    [Fact]
    public async Task OutboxKeepsOneImmutableLogicalEventWhilePublicationMetadataCanAdvance()
    {
        var seed = await SeedAsync();
        var parcel = await PersistParcelAsync(seed);
        var message = OutboxMessage.ParcelCreated(Guid.NewGuid(), parcel, "outbox-test");
        await using (var context = Context())
        {
            context.OutboxMessages.Add(message);
            await context.SaveChangesAsync();
        }

        await using (var duplicateContext = Context())
        {
            duplicateContext.OutboxMessages.Add(OutboxMessage.ParcelCreated(Guid.NewGuid(), parcel, "duplicate-attempt"));
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync());
            AssertPostgresState(duplicate, PostgresErrorCodes.UniqueViolation);
        }

        await using var verification = Context();
        var rewritten = await Assert.ThrowsAsync<PostgresException>(() => verification.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE outbox_messages SET payload = payload || {"{\"recipient\":\"private\"}"}::jsonb WHERE id = {message.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, rewritten.SqlState);
        var deleted = await Assert.ThrowsAsync<PostgresException>(() => verification.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM outbox_messages WHERE id = {message.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, deleted.SqlState);
        await verification.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE outbox_messages SET published_at = {Now.AddMinutes(1)} WHERE id = {message.Id}");
        var saved = await verification.OutboxMessages.SingleAsync(item => item.Id == message.Id);
        Assert.Equal(Now.AddMinutes(1), saved.PublishedAt);
        using var original = JsonDocument.Parse(message.Payload);
        using var persisted = JsonDocument.Parse(saved.Payload);
        Assert.True(JsonElement.DeepEquals(original.RootElement, persisted.RootElement));
    }

    [Theory]
    [InlineData("correlationId", "null", PostgresErrorCodes.CheckViolation)]
    [InlineData("status", "\"Unsupported\"", PostgresErrorCodes.CheckViolation)]
    [InlineData("occurredAt", "\"not-a-date\"", PostgresErrorCodes.InvalidDatetimeFormat)]
    [InlineData("schemaVersion", "\"1\"", PostgresErrorCodes.CheckViolation)]
    public async Task DatabaseRejectsMalformedOutboxContracts(string field, string value, string expectedState)
    {
        var seed = await SeedAsync();
        var parcel = await PersistParcelAsync(seed);
        var message = OutboxMessage.ParcelCreated(Guid.NewGuid(), parcel, "malformed-event");
        var payload = JsonNode.Parse(message.Payload)!.AsObject();
        payload[field] = JsonNode.Parse(value);
        await using var context = Context();
        context.OutboxMessages.Add(message);
        context.Entry(message).Property(item => item.Payload).CurrentValue = payload.ToJsonString();

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        AssertPostgresState(exception, expectedState);
        Assert.False(await context.OutboxMessages.AsNoTracking().AnyAsync(item => item.Id == message.Id));
    }

    [Fact]
    public async Task ConcurrentQuoteConsumptionAllowsOneWriterThroughPostgreSqlXmin()
    {
        var seed = await SeedAsync();
        var firstParcel = await PersistParcelAsync(seed);
        var secondParcel = await PersistParcelAsync(seed);
        await using var firstContext = Context();
        await using var secondContext = Context();
        var firstQuote = await firstContext.Quotes.SingleAsync(item => item.Id == seed.Quote.Id);
        var secondQuote = await secondContext.Quotes.SingleAsync(item => item.Id == seed.Quote.Id);
        firstQuote.Consume(firstParcel, Now.AddMinutes(1));
        secondQuote.Consume(secondParcel, Now.AddMinutes(1));

        var outcomes = await RaceSaveAsync(firstContext, secondContext);

        Assert.Single(outcomes, item => item is null);
        Assert.IsType<DbUpdateConcurrencyException>(Assert.Single(outcomes, item => item is not null));
        await using var verification = Context();
        var consumedBy = (await verification.Quotes.SingleAsync(item => item.Id == seed.Quote.Id)).ConsumedByParcelId;
        Assert.Contains(consumedBy, new Guid?[] { firstParcel.Id, secondParcel.Id });
    }

    [Fact]
    public async Task ConcurrentParcelUpdatesAllowOnlyOneWriterAtTheOriginalVersion()
    {
        var seed = await SeedAsync();
        var parcel = await PersistParcelAsync(seed);
        await using var firstContext = Context();
        await using var secondContext = Context();
        var firstParcel = await firstContext.Parcels.SingleAsync(item => item.Id == parcel.Id);
        var secondParcel = await secondContext.Parcels.SingleAsync(item => item.Id == parcel.Id);
        firstContext.Entry(firstParcel).Property(item => item.Version).CurrentValue = 2;
        firstContext.Entry(firstParcel).Property(item => item.Status).CurrentValue = ParcelStatus.Assigned;
        secondContext.Entry(secondParcel).Property(item => item.Version).CurrentValue = 2;
        secondContext.Entry(secondParcel).Property(item => item.Status).CurrentValue = ParcelStatus.Cancelled;

        var outcomes = await RaceSaveAsync(firstContext, secondContext);

        Assert.Single(outcomes, item => item is null);
        Assert.IsType<DbUpdateConcurrencyException>(Assert.Single(outcomes, item => item is not null));
        await using var verification = Context();
        var saved = await verification.Parcels.SingleAsync(item => item.Id == parcel.Id);
        Assert.Equal(2, saved.Version);
        Assert.Equal(outcomes[0] is null ? ParcelStatus.Assigned : ParcelStatus.Cancelled, saved.Status);
    }

    [Fact]
    public async Task ConcurrentIdempotencyReservationsHaveExactlyOneDatabaseWinner()
    {
        var seed = await SeedAsync();
        var parcel = await PersistParcelAsync(seed);
        var key = Guid.NewGuid().ToString("N");
        await using var first = Context();
        await using var second = Context();
        first.IdempotencyRecords.Add(CompletedRecord(parcel, seed.OwnerId.ToString(), "CreateParcel", key));
        second.IdempotencyRecords.Add(CompletedRecord(parcel, seed.OwnerId.ToString(), "CreateParcel", key));

        var outcomes = await RaceSaveAsync(first, second);

        Assert.Single(outcomes, item => item is null);
        var loser = Assert.IsType<DbUpdateException>(Assert.Single(outcomes, item => item is not null));
        AssertPostgresState(loser, PostgresErrorCodes.UniqueViolation);
        await using var verification = Context();
        Assert.Equal(1, await verification.IdempotencyRecords.CountAsync(item => item.Key == key));
    }

    private DeliveryDbContext Context() => new(new DbContextOptionsBuilder<DeliveryDbContext>()
        .UseNpgsql(fixture.ConnectionString)
        .Options);

    private async Task<Seed> SeedAsync()
    {
        var zone = new DeliveryZone(Guid.NewGuid(), "Test zone " + Guid.NewGuid().ToString("N"));
        var tier = new PricingTier(Guid.NewGuid(), zone.Id, ParcelSize.Small, "test-v1", 450m, 60m);
        var owner = Guid.NewGuid();
        var quote = new Quote(Guid.NewGuid(), owner, Guid.NewGuid().ToString("N"),
            "{\"recipient\":{\"name\":\"Fictional Recipient\",\"phoneNumber\":\"0000000000\"}}", tier, 540m, Now);
        await using var context = Context();
        context.Zones.Add(zone);
        context.PricingTiers.Add(tier);
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();
        return new Seed(zone, tier, owner, quote);
    }

    private async Task<Parcel> PersistParcelAsync(Seed seed, string? tracking = null)
    {
        await using var context = Context();
        var parcel = AddParcel(context, seed, tracking);
        await context.SaveChangesAsync();
        return parcel;
    }

    private static Parcel AddParcel(DeliveryDbContext context, Seed seed, string? tracking = null)
    {
        var parcel = ParcelFor(seed, tracking);
        context.Parcels.Add(parcel);
        context.PriceSnapshots.Add(Snapshot(seed, parcel));
        return parcel;
    }

    private static Parcel ParcelFor(Seed seed, string? tracking = null) => Parcel.ForCustomer(
        Guid.NewGuid(), seed.OwnerId, tracking ?? Guid.NewGuid().ToString("N"), Details(seed), Now);

    private static ParcelDetails Details(Seed seed) => new(ParcelSize.Small, 2.5m, seed.Zone.Id,
        "Fictional pickup", null, "Fictional destination", "Fictional Recipient / 0000000000");

    private static PriceSnapshot Snapshot(Seed seed, Parcel parcel) =>
        new(Guid.NewGuid(), parcel.Id, seed.Tier, 450m, 90m, 540m, Now);

    private static IdempotencyRecord CompletedRecord(Parcel parcel, string principal, string operation, string key,
        string fingerprint = "request-fingerprint")
    {
        var record = new IdempotencyRecord(Guid.NewGuid(), principal, operation, key, fingerprint, Now);
        record.Complete(parcel.Id, 201, "{\"id\":\"" + parcel.Id + "\"}", "/api/v1/parcels/" + parcel.Id);
        return record;
    }

    private static async Task<Exception?[]> RaceSaveAsync(DeliveryDbContext first, DeliveryDbContext second)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Exception?> SaveAsync(DeliveryDbContext context)
        {
            await start.Task;
            return await Record.ExceptionAsync(async () => { await context.SaveChangesAsync(); });
        }

        var firstWrite = SaveAsync(first);
        var secondWrite = SaveAsync(second);
        start.SetResult();
        return await Task.WhenAll(firstWrite, secondWrite);
    }

    private static void AssertPostgresState(DbUpdateException exception, string expectedState) =>
        Assert.Equal(expectedState, Assert.IsType<PostgresException>(exception.InnerException).SqlState);

    private sealed record Seed(DeliveryZone Zone, PricingTier Tier, Guid OwnerId, Quote Quote);
}
