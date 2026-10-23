using System.Globalization;
using System.Text.Json;
using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Tests.Quotes;

public sealed class QuoteApplicationTests
{
    private static readonly Guid OwnerId = Guid.Parse("2b43d56e-bc0f-4b67-b15a-fb71d823c76b");
    private static readonly Guid PickupZoneId = Guid.Parse("f31f3748-d84e-42d6-82a6-b1011ac9f774");
    private static readonly Guid DeliveryZoneId = Guid.Parse("38d54465-4fd0-40f5-81a7-d3c338ed19b9");
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("0.0000001", "450.00")]
    [InlineData("0.5", "450.00")]
    [InlineData("1", "450.00")]
    [InlineData("2.5", "540.00")]
    [InlineData("30", "2190.00")]
    public void PriceUsesDocumentedFormulaIncludingWeightBoundaries(string weight, string expected)
    {
        Assert.True(PriceCalculator.TryCalculate(450m, 60m, Number(weight), out var price));

        Assert.Equal(Number(expected), price.Total);
        Assert.Equal(450m, price.BaseFee);
        Assert.Equal(price.Total, price.BaseFee + price.WeightFee);
    }

    [Theory]
    [InlineData("1.0049", "450.00")]
    [InlineData("1.005", "450.01")]
    [InlineData("1.015", "450.02")]
    [InlineData("1.0149", "450.01")]
    public void PriceRoundsDecimalMidpointsHalfUp(string weight, string expected)
    {
        Assert.True(PriceCalculator.TryCalculate(450m, 1m, Number(weight), out var price));

        Assert.Equal(Number(expected), price.Total);
    }

    [Theory]
    [InlineData("450", "60", "2.5000833333333333333333333333", "90.00", "540.00")]
    [InlineData("450", "60", "2.5000833333333333333333333334", "90.01", "540.01")]
    [InlineData("450", "50", "2.5000999999999999999999999999", "75.00", "525.00")]
    [InlineData("450", "50", "2.5001", "75.01", "525.01")]
    [InlineData("450", "50", "2.5001000000000000000000000001", "75.01", "525.01")]
    [InlineData("0", "0.01", "1.4999999999999999999999999999", "0.00", "0.00")]
    [InlineData("0", "0.01", "1.5", "0.01", "0.01")]
    [InlineData("0", "0.01", "1.5000000000000000000000000001", "0.01", "0.01")]
    [InlineData("0", "0.03", "1.1666666666666666666666666666", "0.00", "0.00")]
    [InlineData("0", "0.03", "1.1666666666666666666666666667", "0.01", "0.01")]
    [InlineData("450.0000", "50.0000", "2.5001", "75.01", "525.01")]
    [InlineData("0", "9999999999999999.99", "2", "9999999999999999.99", "9999999999999999.99")]
    public void PriceRoundsTheExactProductOnceAcrossHalfCentBoundaries(
        string baseFee, string perKg, string weight, string expectedWeightFee, string expectedTotal)
    {
        Assert.True(PriceCalculator.TryCalculate(Number(baseFee), Number(perKg), Number(weight), out var price));

        Assert.Equal(Number(expectedWeightFee), price.WeightFee);
        Assert.Equal(Number(expectedTotal), price.Total);
        Assert.Equal(price.BaseFee + price.WeightFee, price.Total);
    }

    [Fact]
    public void PriceUsesExactDecimalArithmeticAndAllowsZeroRates()
    {
        Assert.True(PriceCalculator.TryCalculate(0.1m, 0.2m, 2m, out var price));
        Assert.Equal(0.3m, price.Total);
        Assert.True(PriceCalculator.TryCalculate(0m, 0m, 30m, out var free));
        Assert.Equal(0m, free.Total);
    }

    [Theory]
    [InlineData("-1", "60", "2")]
    [InlineData("450", "-1", "2")]
    [InlineData("450.001", "60", "2")]
    [InlineData("450", "60.001", "2")]
    [InlineData("450", "60", "0")]
    [InlineData("450", "60", "-0.1")]
    [InlineData("450", "60", "30.0001")]
    [InlineData("10000000000000000", "0", "1")]
    [InlineData("9999999999999999.99", "0.01", "2")]
    [InlineData("0", "9999999999999999.99", "30")]
    public void PriceRejectsInvalidOrUnrepresentableMoney(string baseFee, string perKg, string weight)
    {
        Assert.False(PriceCalculator.TryCalculate(Number(baseFee), Number(perKg), Number(weight), out _));
    }

    [Fact]
    public void PriceAcceptsHighestRepresentableTotal()
    {
        Assert.True(PriceCalculator.TryCalculate(9999999999999999.99m, 0m, 30m, out var price));
        Assert.Equal(9999999999999999.99m, price.Total);
    }

    [Theory]
    [InlineData(false, true, "2b43d56e-bc0f-4b67-b15a-fb71d823c76b", QuoteFailure.Unauthenticated)]
    [InlineData(true, true, null, QuoteFailure.Unauthenticated)]
    [InlineData(true, true, "00000000-0000-0000-0000-000000000000", QuoteFailure.Unauthenticated)]
    [InlineData(true, false, "2b43d56e-bc0f-4b67-b15a-fb71d823c76b", QuoteFailure.Forbidden)]
    public async Task MissingIdentityOrRoleNeverAccessesStorage(
        bool authenticated, bool customer, string? owner, QuoteFailure expected)
    {
        var store = Store();
        var identity = new Customer(authenticated, customer, owner is null ? null : Guid.Parse(owner));
        var estimator = new QuoteEstimator(identity, store, new Clock(Now));

        var result = await estimator.ExecuteAsync(null);

        Assert.Equal(expected, result.Failure);
        Assert.Null(result.Quote);
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, store.SaveCount);
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        var input = Input();
        yield return [null!, "body"];
        yield return [input with { PickupAddress = null }, "pickupAddress"];
        yield return [input with { DeliveryAddress = null }, "deliveryAddress"];
        yield return [input with { PackageDetails = null }, "packageDetails"];
        yield return [input with { Recipient = null }, "recipient"];
        yield return [input with { PickupAddress = input.PickupAddress! with { Street = " " } }, "pickupAddress.street"];
        yield return [input with { PickupAddress = input.PickupAddress! with { City = null } }, "pickupAddress.city"];
        yield return [input with { PickupAddress = input.PickupAddress! with { PostalCode = "\t" } }, "pickupAddress.postalCode"];
        yield return [input with { PickupAddress = input.PickupAddress! with { ZoneId = "not-a-zone" } }, "pickupAddress.zoneId"];
        yield return [input with { DeliveryAddress = input.DeliveryAddress! with { Street = null } }, "deliveryAddress.street"];
        yield return [input with { DeliveryAddress = input.DeliveryAddress! with { City = " " } }, "deliveryAddress.city"];
        yield return [input with { DeliveryAddress = input.DeliveryAddress! with { PostalCode = null } }, "deliveryAddress.postalCode"];
        yield return [input with { DeliveryAddress = input.DeliveryAddress! with { ZoneId = Guid.Empty.ToString() } }, "deliveryAddress.zoneId"];
        yield return [input with { PackageDetails = input.PackageDetails! with { WeightKg = null } }, "packageDetails.weightKg"];
        yield return [input with { PackageDetails = input.PackageDetails! with { WeightKg = 0m } }, "packageDetails.weightKg"];
        yield return [input with { PackageDetails = input.PackageDetails! with { WeightKg = -0.1m } }, "packageDetails.weightKg"];
        yield return [input with { PackageDetails = input.PackageDetails! with { WeightKg = 30.0001m } }, "packageDetails.weightKg"];
        yield return [input with { PackageDetails = input.PackageDetails! with { Size = null } }, "packageDetails.size"];
        yield return [input with { PackageDetails = input.PackageDetails! with { Size = "small" } }, "packageDetails.size"];
        yield return [input with { PackageDetails = input.PackageDetails! with { Size = "0" } }, "packageDetails.size"];
        yield return [input with { PackageDetails = input.PackageDetails! with { Size = "ExtraLarge" } }, "packageDetails.size"];
        yield return [input with { Recipient = input.Recipient! with { Name = " " } }, "recipient.name"];
        yield return [input with { Recipient = input.Recipient! with { PhoneNumber = null } }, "recipient.phoneNumber"];
        yield return [input with { PickupAddress = input.PickupAddress! with { Street = "Pickup\0street" } }, "pickupAddress.street"];
        yield return [input with { PickupAddress = input.PickupAddress! with { City = "Pickup\0city" } }, "pickupAddress.city"];
        yield return [input with { PickupAddress = input.PickupAddress! with { PostalCode = "Postal\0code" } }, "pickupAddress.postalCode"];
        yield return [input with { PickupAddress = input.PickupAddress! with { ZoneId = PickupZoneId + "\0" } }, "pickupAddress.zoneId"];
        yield return [input with { DeliveryAddress = input.DeliveryAddress! with { Street = "Delivery\0street" } }, "deliveryAddress.street"];
        yield return [input with { DeliveryAddress = input.DeliveryAddress! with { City = "Delivery\0city" } }, "deliveryAddress.city"];
        yield return [input with { DeliveryAddress = input.DeliveryAddress! with { PostalCode = "Postal\0code" } }, "deliveryAddress.postalCode"];
        yield return [input with { DeliveryAddress = input.DeliveryAddress! with { ZoneId = DeliveryZoneId + "\0" } }, "deliveryAddress.zoneId"];
        yield return [input with { Recipient = input.Recipient! with { Name = "Recipient\0name" } }, "recipient.name"];
        yield return [input with { Recipient = input.Recipient! with { PhoneNumber = "00000\0number" } }, "recipient.phoneNumber"];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidInputReportsFieldAndNeverAccessesStorage(QuoteInput? input, string field)
    {
        var store = Store();

        var result = await Estimator(store).ExecuteAsync(input);

        Assert.Equal(QuoteFailure.InvalidInput, result.Failure);
        Assert.Contains(field, result.Errors!.Keys);
        Assert.Null(result.Quote);
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, store.SaveCount);
    }

    [Theory]
    [InlineData("Small", ParcelSize.Small)]
    [InlineData("Medium", ParcelSize.Medium)]
    [InlineData("Large", ParcelSize.Large)]
    public void ValidatorSupportsAllContractSizes(string size, ParcelSize expected)
    {
        var result = QuoteInputValidator.Validate(Input() with { PackageDetails = new(30m, size) });

        Assert.Empty(result.Errors);
        Assert.Equal(expected, result.Input!.PackageDetails.Size);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task EitherUnsupportedZonePreventsQuoteCreation(bool pickupActive, bool deliveryActive)
    {
        var store = Store();
        store.Pricing = store.Pricing with { PickupZoneActive = pickupActive, DeliveryZoneActive = deliveryActive };

        var result = await Estimator(store).ExecuteAsync(Input());

        Assert.Equal(QuoteFailure.UnsupportedZone, result.Failure);
        Assert.Equal(0, store.SaveCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task MissingOrAmbiguousActiveTariffsFailClosed(int activeCount)
    {
        var store = Store();
        store.Pricing = store.Pricing with { ActiveTiers = Enumerable.Range(0, activeCount).Select(i => Tier("v" + i)).ToArray() };

        var result = await Estimator(store).ExecuteAsync(Input());

        Assert.Equal(QuoteFailure.PricingUnavailable, result.Failure);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task TariffMustMatchTheRequestedDeliveryZoneAndSize()
    {
        var wrongZone = new PricingTier(Guid.NewGuid(), PickupZoneId, ParcelSize.Small, "v1", 450m, 60m);
        var wrongSize = new PricingTier(Guid.NewGuid(), DeliveryZoneId, ParcelSize.Large, "v1", 450m, 60m);
        var inactive = Tier();
        typeof(PricingTier).GetProperty(nameof(PricingTier.IsActive))!.SetValue(inactive, false);
        foreach (var tier in new[] { wrongZone, wrongSize, inactive })
        {
            var store = Store(tier);

            var result = await Estimator(store).ExecuteAsync(Input());

            Assert.Equal(QuoteFailure.PricingUnavailable, result.Failure);
            Assert.Equal(0, store.SaveCount);
        }
    }

    [Fact]
    public async Task UnrepresentableCalculatedTotalIsPricingFailureWithoutSaving()
    {
        var tier = new PricingTier(Guid.NewGuid(), DeliveryZoneId, ParcelSize.Small, "v1", 9999999999999999.99m, 1m);
        var store = Store(tier);

        var result = await Estimator(store).ExecuteAsync(Input());

        Assert.Equal(QuoteFailure.PricingUnavailable, result.Failure);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task SuccessfulQuoteRetainsOwnerInputTariffAndTenMinuteExpiry()
    {
        var tier = Tier();
        var store = Store(tier);

        var result = await Estimator(store).ExecuteAsync(Input());

        Assert.Null(result.Failure);
        var response = Assert.IsType<QuoteResponse>(result.Quote);
        var quote = Assert.Single(store.Saved);
        Assert.NotEqual(Guid.Empty, response.QuoteId);
        Assert.Equal(quote.Id, response.QuoteId);
        Assert.Equal(OwnerId, quote.OwnerId);
        Assert.Equal(tier.Id, quote.PricingTierId);
        Assert.Equal("test-v1", response.PricingVersion);
        Assert.Equal("LKR", response.Currency);
        Assert.Equal(540m, response.Total);
        Assert.Equal(Now, quote.CreatedAt);
        Assert.Equal(Now.AddMinutes(10), response.ExpiresAt);
        Assert.Equal(quote.RequestFingerprint, response.Fingerprint);
        Assert.Matches("^[a-f0-9]{64}$", response.Fingerprint);
        Assert.Null(quote.ConsumedByParcelId);
        Assert.Equal((PickupZoneId, DeliveryZoneId, ParcelSize.Small), store.RequestedPricing);
        using var input = JsonDocument.Parse(quote.DeliveryInput);
        Assert.Equal(4, input.RootElement.EnumerateObject().Count());
        Assert.Equal(PickupZoneId, input.RootElement.GetProperty("pickupAddress").GetProperty("zoneId").GetGuid());
        Assert.Equal(DeliveryZoneId, input.RootElement.GetProperty("deliveryAddress").GetProperty("zoneId").GetGuid());
        Assert.Equal(2.5m, input.RootElement.GetProperty("packageDetails").GetProperty("weightKg").GetDecimal());
        Assert.Equal("Small", input.RootElement.GetProperty("packageDetails").GetProperty("size").GetString());
        Assert.Equal("Fictional Recipient", input.RootElement.GetProperty("recipient").GetProperty("name").GetString());
        Assert.Equal("0000000000", input.RootElement.GetProperty("recipient").GetProperty("phoneNumber").GetString());
        Assert.DoesNotContain("Fictional", response.Fingerprint);
    }

    [Fact]
    public async Task EquivalentRequestsHaveSameCanonicalInputAndFingerprintAcrossTime()
    {
        var store = Store();
        var clock = new Clock(Now);
        var estimator = new QuoteEstimator(CustomerIdentity(), store, clock);
        var first = await estimator.ExecuteAsync(Input());
        clock.UtcNow = Now.AddMinutes(20);
        var equivalent = Input() with
        {
            PickupAddress = new(" Fictional Pickup ", " Colombo ", " 00100 ", PickupZoneId.ToString("B").ToUpperInvariant()),
            DeliveryAddress = new(" Fictional Delivery ", " Kandy ", " 20000 ", DeliveryZoneId.ToString("N").ToUpperInvariant()),
            PackageDetails = new(2.500m, "Small"),
            Recipient = new(" Fictional Recipient ", " 0000000000 ")
        };

        var second = await estimator.ExecuteAsync(equivalent);

        Assert.Equal(first.Quote!.Fingerprint, second.Quote!.Fingerprint);
        Assert.Equal(store.Saved[0].DeliveryInput, store.Saved[1].DeliveryInput);
        Assert.NotEqual(first.Quote.QuoteId, second.Quote.QuoteId);
        Assert.True(first.Quote.ExpiresAt < clock.UtcNow);
        Assert.Equal(clock.UtcNow.AddMinutes(10), second.Quote.ExpiresAt);
    }

    [Fact]
    public async Task FingerprintBindsOwnerRecipientAndRetainedPricingVersion()
    {
        var tier = Tier();
        var original = await Estimator(Store(tier)).ExecuteAsync(Input());
        var otherOwner = await new QuoteEstimator(new Customer(true, true, Guid.NewGuid()), Store(tier), new Clock(Now)).ExecuteAsync(Input());
        var otherRecipient = await Estimator(Store(tier)).ExecuteAsync(Input() with { Recipient = new("Another Fictional Recipient", "0000000000") });
        var otherVersion = await Estimator(Store(Tier("test-v2"))).ExecuteAsync(Input());

        Assert.Equal(4, new[] { original, otherOwner, otherRecipient, otherVersion }.Select(x => x.Quote!.Fingerprint).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreFailureReturnsSafeApplicationFailure(bool failSave)
    {
        var store = Store();
        var failure = new QuoteStoreUnavailableException("Private provider detail must never become an application response.");
        if (failSave)
        {
            store.SaveFailure = failure;
        }
        else
        {
            store.ReadFailure = failure;
        }

        var result = await Estimator(store).ExecuteAsync(Input());

        Assert.Equal(QuoteFailure.PersistenceUnavailable, result.Failure);
        Assert.Null(result.Quote);
        Assert.Null(result.Errors);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task RequestCancellationIsNotReportedAsStorageFailure()
    {
        var store = Store();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        store.ReadFailure = new OperationCanceledException(cancellation.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => Estimator(store).ExecuteAsync(Input(), cancellation.Token));

        Assert.Equal(cancellation.Token, store.CancellationToken);
        Assert.Empty(store.Saved);
    }

    private static decimal Number(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static QuoteInput Input() => new(
        new("Fictional Pickup", "Colombo", "00100", PickupZoneId.ToString()),
        new("Fictional Delivery", "Kandy", "20000", DeliveryZoneId.ToString()),
        new(2.5m, "Small"), new("Fictional Recipient", "0000000000"));

    private static PricingTier Tier(string version = "test-v1") => new(
        Guid.NewGuid(), DeliveryZoneId, ParcelSize.Small, version, 450m, 60m);

    private static FakeStore Store(PricingTier? tier = null) => new(new(true, true, [tier ?? Tier()]));
    private static Customer CustomerIdentity() => new(true, true, OwnerId);
    private static QuoteEstimator Estimator(FakeStore store) => new(CustomerIdentity(), store, new Clock(Now));

    private sealed record Customer(bool IsAuthenticated, bool IsCustomer, Guid? CustomerId) : ICurrentCustomer;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FakeStore(QuotePricingData pricing) : IQuoteStore
    {
        public QuotePricingData Pricing { get; set; } = pricing;
        public List<Quote> Saved { get; } = [];
        public int ReadCount { get; private set; }
        public int SaveCount { get; private set; }
        public Exception? ReadFailure { get; set; }
        public Exception? SaveFailure { get; set; }
        public (Guid Pickup, Guid Delivery, ParcelSize Size) RequestedPricing { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<QuotePricingData> ReadPricingAsync(
            Guid pickupZoneId, Guid deliveryZoneId, ParcelSize size, CancellationToken cancellationToken)
        {
            ReadCount++;
            RequestedPricing = (pickupZoneId, deliveryZoneId, size);
            CancellationToken = cancellationToken;
            return ReadFailure is { } exception ? Task.FromException<QuotePricingData>(exception) : Task.FromResult(Pricing);
        }

        public Task SaveAsync(Quote quote, CancellationToken cancellationToken)
        {
            SaveCount++;
            CancellationToken = cancellationToken;
            if (SaveFailure is { } exception)
            {
                return Task.FromException(exception);
            }

            Saved.Add(quote);
            return Task.CompletedTask;
        }
    }
}
