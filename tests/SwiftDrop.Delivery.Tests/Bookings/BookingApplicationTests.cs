using System.Text.Json;
using System.Text.Json.Nodes;
using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Tests.Bookings;

public sealed class BookingApplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid OwnerId = Guid.Parse("22ecf15e-aa3e-44a5-8761-8ca55ba198c9");
    private const string Key = "opaque-booking-key";

    [Theory]
    [InlineData(false, true, "22ecf15e-aa3e-44a5-8761-8ca55ba198c9", BookingFailure.Unauthenticated)]
    [InlineData(true, true, null, BookingFailure.Unauthenticated)]
    [InlineData(true, true, "00000000-0000-0000-0000-000000000000", BookingFailure.Unauthenticated)]
    [InlineData(true, false, "22ecf15e-aa3e-44a5-8761-8ca55ba198c9", BookingFailure.Forbidden)]
    public async Task AuthorizationPrecedesInputAndStorage(bool authenticated, bool isCustomer, string? owner, BookingFailure failure)
    {
        var store = new Store(Seed());
        var tracker = new Tracker();
        var customer = new Customer(authenticated, isCustomer, owner is null ? null : Guid.Parse(owner));

        var result = await new CustomerBookingHandler(customer, store, tracker, new Clock(Now)).ExecuteAsync(null, null, "test");

        Assert.Equal(failure, result.Failure);
        Assert.Null(result.Response);
        Assert.Equal(0, store.BeginCount);
        Assert.Equal(0, tracker.Calls);
    }

    public static IEnumerable<object[]> InvalidConfirmations()
    {
        var valid = new ConfirmInput(Guid.NewGuid().ToString(), 540m);
        yield return [null!, Key, "body"];
        yield return [valid with { QuoteId = null }, Key, "quoteId"];
        yield return [valid with { QuoteId = "invalid" }, Key, "quoteId"];
        yield return [valid with { QuoteId = Guid.Empty.ToString() }, Key, "quoteId"];
        yield return [valid with { AcceptTotal = null }, Key, "acceptTotal"];
        yield return [valid with { AcceptTotal = -0.01m }, Key, "acceptTotal"];
        yield return [valid with { AcceptTotal = 10000000000000000m }, Key, "acceptTotal"];
        yield return [valid with { AcceptTotal = 540.001m }, Key, "acceptTotal"];
        yield return [valid, null!, "Idempotency-Key"];
        yield return [valid, " ", "Idempotency-Key"];
        yield return [valid, new string('k', 129), "Idempotency-Key"];
        yield return [valid, "key\0suffix", "Idempotency-Key"];
        yield return [valid, "key\tsuffix", "Idempotency-Key"];
        yield return [valid, "key\nsuffix", "Idempotency-Key"];
    }

    [Theory]
    [MemberData(nameof(InvalidConfirmations))]
    public async Task InvalidConfirmationNeverOpensTransaction(ConfirmInput? input, string? key, string field)
    {
        var store = new Store(Seed());
        var tracker = new Tracker();

        var result = await Handler(store, tracker).ExecuteAsync(input, key, "test");

        Assert.Equal(BookingFailure.InvalidInput, result.Failure);
        Assert.Contains(field, result.Errors!.Keys);
        Assert.Equal(0, store.BeginCount);
        Assert.Equal(0, tracker.Calls);
    }

    [Fact]
    public void ConfirmationFingerprintCanonicalizesQuoteAndDecimalWithoutChangingOpaqueKey()
    {
        var quoteId = Guid.NewGuid();
        var first = BookingInputValidator.Validate(new(quoteId.ToString("D"), 540m), " key ").Input!;
        var equivalent = BookingInputValidator.Validate(new(" " + quoteId.ToString("B").ToUpperInvariant() + " ", 540.000m), " key ").Input!;
        var otherTotal = BookingInputValidator.Validate(new(quoteId.ToString(), 541m), " key ").Input!;
        var otherQuote = BookingInputValidator.Validate(new(Guid.NewGuid().ToString(), 540m), " key ").Input!;

        Assert.Equal(first.Fingerprint, equivalent.Fingerprint);
        Assert.Equal(" key ", equivalent.IdempotencyKey);
        Assert.Equal(3, new[] { first.Fingerprint, otherTotal.Fingerprint, otherQuote.Fingerprint }.Distinct().Count());
        Assert.Matches("^[a-f0-9]{64}$", first.Fingerprint);
    }

    [Fact]
    public async Task ConfirmationAtomicallyBuildsCompleteCreatedBookingAndExactResponse()
    {
        var seed = Seed();
        var store = new Store(seed);
        var clock = new Clock(Now.AddMinutes(1).AddTicks(7));
        var tracker = new Tracker();

        var result = await Handler(store, tracker, clock).ExecuteAsync(Input(seed), " key ", "correlation-test");

        Assert.Null(result.Failure);
        var response = Assert.IsType<StoredBookingResponse>(result.Response);
        Assert.Equal(201, response.StatusCode);
        var transaction = Assert.Single(store.Transactions);
        Assert.Equal(new[] { "reserve", "load", "save", "commit", "dispose" }, transaction.Calls);
        Assert.True(transaction.Committed);
        Assert.True(transaction.Disposed);
        var write = Assert.IsType<BookingWriteSet>(transaction.Write);
        Assert.Equal(OwnerId, write.Parcel.CustomerId);
        Assert.Null(write.Parcel.MerchantId);
        Assert.Equal(ParcelStatus.Created, write.Parcel.Status);
        Assert.Equal(1, write.Parcel.Version);
        Assert.Equal(seed.Tier.ZoneId, write.Parcel.ZoneId);
        Assert.Null(write.Parcel.PickupContact);
        Assert.Equal(0, write.Parcel.CreatedAt.Ticks % 10);
        Assert.Equal(Now.AddMinutes(1), write.Parcel.CreatedAt);
        Assert.Equal(write.Parcel.CreatedAt, write.Parcel.UpdatedAt);
        Assert.Equal(write.Parcel.CreatedAt, write.Snapshot.CalculatedAt);
        Assert.Equal(write.Parcel.CreatedAt, write.History.Timestamp);
        Assert.Equal(write.Parcel.CreatedAt, write.Outbox.CreatedAt);
        Assert.Equal(write.Parcel.Id, transaction.SavedQuote!.ConsumedByParcelId);
        Assert.Equal(" key ", transaction.Record!.Key);
        Assert.Equal(OwnerId.ToString("D"), transaction.Record.PrincipalId);
        Assert.Equal("CreateParcel", transaction.Record.Operation);
        Assert.Equal(seed.Tier.Id, write.Snapshot.PricingTierId);
        Assert.Equal(seed.Tier.PricingVersion, write.Snapshot.PricingVersion);
        Assert.Equal(450m, write.Snapshot.BaseFee);
        Assert.Equal(90m, write.Snapshot.WeightFee);
        Assert.Equal(540m, write.Snapshot.Total);
        Assert.Equal(OwnerId, write.History.ChangedBy);
        Assert.Null(write.History.FromStatus);
        Assert.Equal(ParcelStatus.Created, write.History.ToStatus);
        Assert.Equal("/api/v1/parcels/" + write.Parcel.Id, response.Location);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal(6, body.RootElement.EnumerateObject().Count());
        Assert.Equal(write.Parcel.Id, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Created", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("priceSnapshot").EnumerateObject().Count());
        using var envelope = JsonDocument.Parse(transaction.Record.StoredResponse!);
        Assert.Equal(response.Body, envelope.RootElement.GetProperty("body").GetString());
        Assert.Equal(response.Location, envelope.RootElement.GetProperty("location").GetString());
        using var pickup = JsonDocument.Parse(write.Parcel.PickupAddress);
        using var delivery = JsonDocument.Parse(write.Parcel.DropoffAddress);
        using var contact = JsonDocument.Parse(write.Parcel.DropoffContact);
        Assert.Equal(seed.PickupZoneId, pickup.RootElement.GetProperty("zoneId").GetGuid());
        Assert.Equal(seed.Tier.ZoneId, delivery.RootElement.GetProperty("zoneId").GetGuid());
        Assert.Equal("Fictional Recipient", contact.RootElement.GetProperty("name").GetString());
        Assert.DoesNotContain("Fictional", write.Outbox.Payload);
        using var eventPayload = JsonDocument.Parse(write.Outbox.Payload);
        Assert.Equal("correlation-test", eventPayload.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(write.Parcel.CreatedAt, eventPayload.RootElement.GetProperty("occurredAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task MissingAndForeignQuoteReturnSameFailureWithoutCreation()
    {
        foreach (var foreign in new[] { false, true })
        {
            var seed = Seed(foreign ? Guid.NewGuid() : OwnerId);
            var store = new Store(seed) { ReturnMissingQuote = !foreign };
            var tracker = new Tracker();

            var result = await Handler(store, tracker).ExecuteAsync(Input(seed), Key, "test");

            Assert.Equal(BookingFailure.NotFound, result.Failure);
            Assert.False(Assert.Single(store.Transactions).Committed);
            Assert.Equal(0, tracker.Calls);
        }
    }

    [Fact]
    public async Task ExpiryIsEvaluatedAfterTheQuoteLockWait()
    {
        var seed = Seed();
        var clock = new Clock(Now.AddMinutes(9));
        var store = new Store(seed) { OnLoad = () => clock.UtcNow = Now.AddMinutes(10) };
        var tracker = new Tracker();

        var result = await Handler(store, tracker, clock).ExecuteAsync(Input(seed), Key, "test");

        Assert.Equal(BookingFailure.QuoteExpired, result.Failure);
        Assert.Equal(0, tracker.Calls);
        Assert.Null(Assert.Single(store.Transactions).Write);
    }

    [Fact]
    public async Task ConsumedQuoteAndChangedAcceptedAmountCannotCreateAnotherParcel()
    {
        var consumed = Seed();
        var parcel = Parcel.ForCustomer(Guid.NewGuid(), OwnerId, "earlier", Details(consumed), Now);
        consumed.Quote.Consume(parcel, Now);
        var store = new Store(consumed);
        var consumedResult = await Handler(store).ExecuteAsync(Input(consumed), Key, "test");
        Assert.Equal(BookingFailure.QuoteConsumed, consumedResult.Failure);
        Assert.Null(Assert.Single(store.Transactions).Write);

        var changed = Seed();
        var changedStore = new Store(changed);
        var priceResult = await Handler(changedStore).ExecuteAsync(Input(changed) with { AcceptTotal = 541m }, Key, "test");
        Assert.Equal(BookingFailure.PriceChanged, priceResult.Failure);
        Assert.Null(Assert.Single(changedStore.Transactions).Write);
    }

    [Fact]
    public async Task StillValidQuoteHonorsInactiveRetainedTariffAndExactOriginalComponents()
    {
        var seed = Seed(weight: 2.5000833333333333333333333333m);
        Set(seed.Tier, nameof(PricingTier.IsActive), false);
        var store = new Store(seed);

        var result = await Handler(store).ExecuteAsync(Input(seed), Key, "test");

        Assert.Null(result.Failure);
        Assert.Equal(540m, Assert.Single(store.Transactions).Write!.Snapshot.Total);
        Assert.Equal(90m, store.Transactions[0].Write!.Snapshot.WeightFee);
        Assert.Equal(seed.Tier.Id, store.Transactions[0].Write!.Snapshot.PricingTierId);
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("recipient")]
    [InlineData("missingInput")]
    [InlineData("unknownInput")]
    [InlineData("roundedWeight")]
    [InlineData("tierId")]
    [InlineData("tierZone")]
    [InlineData("tierSize")]
    [InlineData("tierVersion")]
    [InlineData("currency")]
    [InlineData("price")]
    [InlineData("quoteId")]
    public async Task InvalidRetainedQuoteIntegrityCannotReachCreation(string corruption)
    {
        var seed = Seed();
        switch (corruption)
        {
            case "fingerprint": Set(seed.Quote, nameof(Quote.RequestFingerprint), new string('0', 64)); break;
            case "recipient": Set(seed.Quote, nameof(Quote.DeliveryInput), seed.Quote.DeliveryInput.Replace("Fictional Recipient", "Changed Recipient", StringComparison.Ordinal)); break;
            case "missingInput": Set(seed.Quote, nameof(Quote.DeliveryInput), "{}"); break;
            case "unknownInput": Set(seed.Quote, nameof(Quote.DeliveryInput), seed.Quote.DeliveryInput.Replace("{", "{\"userId\":\"override\",", StringComparison.Ordinal)); break;
            case "roundedWeight": Set(seed.Quote, nameof(Quote.DeliveryInput), seed.Quote.DeliveryInput.Replace("2.5", "2.50000000000000000000000000001", StringComparison.Ordinal)); break;
            case "tierId": seed = seed with { Tier = new(Guid.NewGuid(), seed.Tier.ZoneId, ParcelSize.Small, "v1", 450m, 60m) }; break;
            case "tierZone": Set(seed.Tier, nameof(PricingTier.ZoneId), Guid.NewGuid()); break;
            case "tierSize": Set(seed.Tier, nameof(PricingTier.Size), ParcelSize.Large); break;
            case "tierVersion": Set(seed.Quote, nameof(Quote.PricingVersion), "changed"); break;
            case "currency": Set(seed.Quote, nameof(Quote.Currency), "USD"); break;
            case "price": Set(seed.Tier, nameof(PricingTier.PerKgRate), 61m); break;
        }
        var store = new Store(seed);
        var requested = Input(seed);
        if (corruption == "quoteId")
        {
            requested = requested with { QuoteId = Guid.NewGuid().ToString() };
        }
        var tracker = new Tracker();

        var result = await Handler(store, tracker).ExecuteAsync(requested, Key, "test");

        Assert.Equal(BookingFailure.QuoteConflict, result.Failure);
        Assert.Equal(0, tracker.Calls);
        Assert.Null(Assert.Single(store.Transactions).Write);
    }

    [Fact]
    public async Task JsonbPropertyOrderDoesNotChangeQuoteFingerprint()
    {
        var seed = Seed();
        var original = JsonNode.Parse(seed.Quote.DeliveryInput)!.AsObject();
        var reordered = new JsonObject();
        foreach (var item in original.Reverse())
        {
            reordered[item.Key] = item.Value!.DeepClone();
        }
        Set(seed.Quote, nameof(Quote.DeliveryInput), reordered.ToJsonString());

        var result = await Handler(new Store(seed)).ExecuteAsync(Input(seed), Key, "test");

        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task IdenticalCommittedRetryReturnsOriginalBytesBeforeCheckingExpiredConsumedQuote()
    {
        var seed = Seed();
        var firstStore = new Store(seed);
        var first = await Handler(firstStore).ExecuteAsync(Input(seed), Key, "first-correlation");
        var record = Assert.Single(firstStore.Transactions).Record!;
        var replayStore = new Store(seed) { ExistingRecord = record, ReturnMissingQuote = true };
        var tracker = new Tracker();

        var replay = await Handler(replayStore, tracker, new Clock(Now.AddMinutes(20)))
            .ExecuteAsync(Input(seed), Key, "different-correlation");

        Assert.Null(replay.Failure);
        Assert.Equal(first.Response, replay.Response);
        var transaction = Assert.Single(replayStore.Transactions);
        Assert.Equal(new[] { "reserve", "dispose" }, transaction.Calls);
        Assert.Equal(0, tracker.Calls);
        Assert.Null(transaction.Write);
    }

    [Fact]
    public async Task ChangedIdempotencyPayloadAndExpiredKeyAreRejectedBeforeQuoteLookup()
    {
        var seed = Seed();
        var firstStore = new Store(seed);
        await Handler(firstStore).ExecuteAsync(Input(seed), Key, "test");
        var record = firstStore.Transactions[0].Record!;
        var conflictStore = new Store(seed) { ExistingRecord = record };
        var conflict = await Handler(conflictStore).ExecuteAsync(Input(seed) with { AcceptTotal = 541m }, Key, "test");
        Assert.Equal(BookingFailure.KeyConflict, conflict.Failure);
        Assert.Equal(new[] { "reserve", "dispose" }, conflictStore.Transactions[0].Calls);

        var expiredStore = new Store(seed) { ExistingRecord = record };
        var expired = await Handler(expiredStore, clock: new Clock(record.ExpiresAt)).ExecuteAsync(Input(seed), Key, "test");
        Assert.Equal(BookingFailure.KeyExpired, expired.Failure);
        Assert.Equal(new[] { "reserve", "dispose" }, expiredStore.Transactions[0].Calls);

        var changedExpiredStore = new Store(seed) { ExistingRecord = record };
        var changedExpired = await Handler(changedExpiredStore, clock: new Clock(record.ExpiresAt))
            .ExecuteAsync(Input(seed) with { AcceptTotal = 541m }, Key, "test");
        Assert.Equal(BookingFailure.KeyExpired, changedExpired.Failure);
        Assert.Equal(new[] { "reserve", "dispose" }, changedExpiredStore.Transactions[0].Calls);
    }

    [Fact]
    public async Task IncompleteReservationAndForeignReservationDoNotLoadQuote()
    {
        var seed = Seed();
        var fingerprint = BookingInputValidator.Validate(Input(seed), Key).Input!.Fingerprint;
        foreach (var foreign in new[] { false, true })
        {
            var record = new IdempotencyRecord(Guid.NewGuid(), (foreign ? Guid.NewGuid() : OwnerId).ToString(),
                "CreateParcel", Key, fingerprint, Now);
            var store = new Store(seed) { ExistingRecord = record };

            var result = await Handler(store).ExecuteAsync(Input(seed), Key, "test");

            Assert.Equal(foreign ? BookingFailure.PersistenceUnavailable : BookingFailure.OperationInProgress, result.Failure);
            Assert.Equal(new[] { "reserve", "dispose" }, store.Transactions[0].Calls);
        }
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("wrongLocation")]
    [InlineData("wrongStatus")]
    [InlineData("privateField")]
    [InlineData("oversizedVersion")]
    [InlineData("oversizedAmount")]
    public async Task MalformedStoredReplayReturnsSafeStorageFailure(string corruption)
    {
        var seed = Seed();
        var initialStore = new Store(seed);
        await Handler(initialStore).ExecuteAsync(Input(seed), Key, "test");
        var record = initialStore.Transactions[0].Record!;
        var envelope = JsonNode.Parse(record.StoredResponse!)!.AsObject();
        switch (corruption)
        {
            case "not-json": Set(record, nameof(IdempotencyRecord.StoredResponse), "not-json"); break;
            case "{}": Set(record, nameof(IdempotencyRecord.StoredResponse), "{}"); break;
            case "wrongLocation": envelope["location"] = "/private/elsewhere"; break;
            case "wrongStatus": envelope["statusCode"] = 200; break;
            default:
                var body = JsonNode.Parse(envelope["body"]!.GetValue<string>())!.AsObject();
                if (corruption == "privateField") body["recipient"] = "private";
                if (corruption == "oversizedVersion") body["version"] = JsonNode.Parse("999999999999999999999999999999999999");
                if (corruption == "oversizedAmount") body["priceSnapshot"]!["total"] = JsonNode.Parse("999999999999999999999999999999999999");
                envelope["body"] = body.ToJsonString();
                break;
        }
        if (corruption is not ("not-json" or "{}")) Set(record, nameof(IdempotencyRecord.StoredResponse), envelope.ToJsonString());
        var store = new Store(seed) { ExistingRecord = record };

        var result = await Handler(store).ExecuteAsync(Input(seed), Key, "test");

        Assert.Equal(BookingFailure.PersistenceUnavailable, result.Failure);
        Assert.Null(result.Response);
        Assert.Equal(new[] { "reserve", "dispose" }, store.Transactions[0].Calls);
    }

    [Theory]
    [InlineData("reserve")]
    [InlineData("load")]
    [InlineData("save")]
    [InlineData("commit")]
    public async Task StorageFailureDisposesTransactionWithoutReportingSuccess(string stage)
    {
        var seed = Seed();
        var store = new Store(seed) { FailureStage = stage, Failure = BookingStorageFailure.Unavailable };

        var result = await Handler(store).ExecuteAsync(Input(seed), Key, "test");

        Assert.Equal(BookingFailure.PersistenceUnavailable, result.Failure);
        Assert.Null(result.Response);
        var transaction = Assert.Single(store.Transactions);
        Assert.True(transaction.Disposed);
        Assert.False(transaction.Committed);
    }

    [Theory]
    [InlineData(BookingStorageFailure.InProgress)]
    [InlineData(BookingStorageFailure.ConcurrentWrite)]
    public async Task StorageConcurrencyFailureProducesRetryableConflict(BookingStorageFailure failure)
    {
        var seed = Seed();
        var store = new Store(seed) { FailureStage = "reserve", Failure = failure };

        var result = await Handler(store).ExecuteAsync(Input(seed), Key, "test");

        Assert.Equal(BookingFailure.OperationInProgress, result.Failure);
        Assert.True(Assert.Single(store.Transactions).Disposed);
    }

    [Theory]
    [InlineData(1, true, 2)]
    [InlineData(2, true, 3)]
    [InlineData(3, false, 3)]
    public async Task TrackingCollisionRetriesEntireTransactionAtMostThreeTimes(int collisions, bool succeeds, int attempts)
    {
        var seed = Seed();
        var store = new Store(seed) { TrackingCollisions = collisions };
        var tracker = new Tracker();

        var result = await Handler(store, tracker).ExecuteAsync(Input(seed), Key, "test");

        Assert.Equal(attempts, store.BeginCount);
        Assert.Equal(attempts, tracker.Calls);
        Assert.All(store.Transactions, item => Assert.True(item.Disposed));
        Assert.Equal(succeeds ? 1 : 0, store.Transactions.Count(item => item.Committed));
        Assert.Equal(succeeds ? (BookingFailure?)null : BookingFailure.PersistenceUnavailable, result.Failure);
        Assert.Equal(succeeds, result.Response is not null);
        Assert.Single(store.Transactions.Select(item => item.Record!.RequestFingerprint).Distinct());
        Assert.Single(store.Transactions.Select(item => item.Record!.Key).Distinct());
    }

    [Fact]
    public async Task CancellationPropagatesWhileDisposingTheTransaction()
    {
        var seed = Seed();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new Store(seed) { Cancellation = cancellation.Token };

        await Assert.ThrowsAsync<OperationCanceledException>(() => Handler(store).ExecuteAsync(Input(seed), Key, "test", cancellation.Token));

        Assert.True(Assert.Single(store.Transactions).Disposed);
        Assert.False(store.Transactions[0].Committed);
    }

    [Fact]
    public void SharedAssemblerSupportsAuthorizedMerchantOriginAndRejectsMismatchedPricing()
    {
        var seed = Seed();
        Assert.True(PriceCalculator.TryCalculate(450m, 60m, 2.5m, out var price));
        var merchant = Guid.NewGuid();
        var parcel = Parcel.ForMerchant(Guid.NewGuid(), merchant, "external-order", "tracking", Details(seed), Now);

        var write = BookingAssembler.Create(parcel, seed.Tier, price, merchant, "merchant-correlation");

        Assert.Null(write.Parcel.CustomerId);
        Assert.Equal(merchant, write.Parcel.MerchantId);
        Assert.Equal(merchant, write.History.ChangedBy);
        Assert.Throws<ArgumentException>(() => BookingAssembler.Create(parcel, seed.Tier, price with { Total = 999m }, merchant, "test"));
        using var payload = JsonDocument.Parse(write.Outbox.Payload);
        Assert.Equal(merchant.ToString(), payload.RootElement.GetProperty("merchantId").GetString());
    }

    private static CustomerBookingHandler Handler(Store store, Tracker? tracker = null, Clock? clock = null) =>
        new(new Customer(true, true, OwnerId), store, tracker ?? new Tracker(), clock ?? new Clock(Now));

    private static ConfirmInput Input(BookingSeed seed) => new(seed.Quote.Id.ToString(), seed.Quote.Total);

    private static BookingSeed Seed(Guid? owner = null, decimal weight = 2.5m)
    {
        var pickupZone = Guid.NewGuid();
        var deliveryZone = Guid.NewGuid();
        var tier = new PricingTier(Guid.NewGuid(), deliveryZone, ParcelSize.Small, "test-v1", 450m, 60m);
        var input = new QuoteInput(new("Fictional Pickup", "Colombo", "00100", pickupZone.ToString()),
            new("Fictional Delivery", "Kandy", "20000", deliveryZone.ToString()),
            new(weight, "Small"), new("Fictional Recipient", "0000000000"));
        var canonical = QuoteCanonicalInput.Serialize(QuoteInputValidator.Validate(input).Input!);
        Assert.True(PriceCalculator.TryCalculate(tier.BaseRate, tier.PerKgRate, weight, out var price));
        var principal = owner ?? OwnerId;
        var quote = new Quote(Guid.NewGuid(), principal, QuoteCanonicalInput.Fingerprint(principal, canonical, tier), canonical, tier, price.Total, Now);
        return new(quote, tier, pickupZone);
    }

    private static ParcelDetails Details(BookingSeed seed) => new(ParcelSize.Small, 2.5m, seed.Tier.ZoneId,
        "fictional pickup", null, "fictional delivery", "fictional contact");

    private static Quote Clone(Quote quote, PricingTier tier)
    {
        // Preserve deliberately malformed persisted fields without invoking constructor guards.
        var clone = new Quote(quote.Id, quote.OwnerId, quote.RequestFingerprint, quote.DeliveryInput,
            tier, quote.Total, quote.CreatedAt);
        Set(clone, nameof(Quote.PricingTierId), quote.PricingTierId);
        Set(clone, nameof(Quote.PricingVersion), quote.PricingVersion);
        Set(clone, nameof(Quote.Currency), quote.Currency);
        Set(clone, nameof(Quote.ConsumedByParcelId), quote.ConsumedByParcelId);
        return clone;
    }

    private static void Set(object target, string property, object? value) => target.GetType().GetProperty(property)!.SetValue(target, value);

    private sealed record BookingSeed(Quote Quote, PricingTier Tier, Guid PickupZoneId);
    private sealed record Customer(bool IsAuthenticated, bool IsCustomer, Guid? CustomerId) : ICurrentCustomer;
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class Tracker : ITrackingNumberGenerator
    {
        public int Calls { get; private set; }
        public string Generate() => "TRK-" + (++Calls).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class Store(BookingSeed seed) : IBookingStore
    {
        public int BeginCount { get; private set; }
        public List<Transaction> Transactions { get; } = [];
        public bool ReturnMissingQuote { get; init; }
        public IdempotencyRecord? ExistingRecord { get; init; }
        public Action? OnLoad { get; init; }
        public string? FailureStage { get; init; }
        public BookingStorageFailure Failure { get; init; }
        public int TrackingCollisions { get; init; }
        public CancellationToken? Cancellation { get; init; }

        public Task<IBookingTransaction> BeginAsync(CancellationToken cancellationToken)
        {
            BeginCount++;
            var transaction = new Transaction(this, new BookingQuote(Clone(seed.Quote, seed.Tier), seed.Tier), BeginCount);
            Transactions.Add(transaction);
            return Task.FromResult<IBookingTransaction>(transaction);
        }
    }

    private sealed class Transaction(Store store, BookingQuote quote, int attempt) : IBookingTransaction
    {
        public List<string> Calls { get; } = [];
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }
        public BookingWriteSet? Write { get; private set; }
        public Quote? SavedQuote { get; private set; }
        public IdempotencyRecord? Record { get; private set; }

        public Task<BookingReservation> ReserveAsync(Guid ownerId, string key, string fingerprint, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls.Add("reserve");
            Fail("reserve");
            if (store.Cancellation is { } cancellation) throw new OperationCanceledException(cancellation);
            Record = store.ExistingRecord ?? new IdempotencyRecord(Guid.NewGuid(), ownerId.ToString("D"), "CreateParcel", key, fingerprint, now);
            return Task.FromResult(new BookingReservation(store.ExistingRecord is null, Record));
        }

        public Task<BookingQuote?> LoadOwnedQuoteForUpdateAsync(Guid ownerId, Guid quoteId, CancellationToken cancellationToken)
        {
            Calls.Add("load");
            Fail("load");
            store.OnLoad?.Invoke();
            return Task.FromResult<BookingQuote?>(store.ReturnMissingQuote ? null : quote);
        }

        public Task SaveAsync(Quote savedQuote, BookingWriteSet write, IdempotencyRecord reservation, CancellationToken cancellationToken)
        {
            Calls.Add("save");
            Write = write;
            SavedQuote = savedQuote;
            Record = reservation;
            Fail("save");
            if (attempt <= store.TrackingCollisions) throw new BookingStoreException(BookingStorageFailure.TrackingCollision);
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Calls.Add("commit");
            Fail("commit");
            Committed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Calls.Add("dispose");
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private void Fail(string stage)
        {
            if (store.FailureStage == stage) throw new BookingStoreException(store.Failure);
        }
    }
}
