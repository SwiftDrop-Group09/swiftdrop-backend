using System.Text.Json;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Tests;

public sealed class DomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("0")]
    [InlineData("-0.1")]
    [InlineData("30.0001")]
    public void ParcelRejectsWeightOutsideDocumentedBounds(string weight)
    {
        var details = Details() with { WeightKg = decimal.Parse(weight, System.Globalization.CultureInfo.InvariantCulture) };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Parcel.ForCustomer(Guid.NewGuid(), Guid.NewGuid(), "test-tracking", details, Now));
    }

    [Theory]
    [InlineData("0.0001")]
    [InlineData("30")]
    public void ParcelAcceptsPositiveWeightUpToThirtyKilograms(string weight)
    {
        var details = Details() with { WeightKg = decimal.Parse(weight, System.Globalization.CultureInfo.InvariantCulture) };
        var owner = Guid.NewGuid();

        var parcel = Parcel.ForCustomer(Guid.NewGuid(), owner, "test-tracking", details, Now);

        Assert.Equal(details.WeightKg, parcel.WeightKg);
        Assert.Equal(owner, parcel.CustomerId);
        Assert.Null(parcel.MerchantId);
        Assert.Null(parcel.ExternalOrderId);
        Assert.Equal(ParcelStatus.Created, parcel.Status);
        Assert.Equal(1, parcel.Version);
    }

    [Fact]
    public void ParcelRejectsUnsupportedSizeAndMissingOriginOrRequiredDetails()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Parcel.ForCustomer(Guid.NewGuid(), Guid.NewGuid(), "test", Details() with { Size = (ParcelSize)99 }, Now));
        Assert.Throws<ArgumentException>(() =>
            Parcel.ForCustomer(Guid.NewGuid(), Guid.Empty, "test", Details(), Now));
        Assert.Throws<ArgumentException>(() =>
            Parcel.ForMerchant(Guid.NewGuid(), Guid.NewGuid(), " ", "test", Details(), Now));
        Assert.Throws<ArgumentException>(() =>
            Parcel.ForCustomer(Guid.NewGuid(), Guid.NewGuid(), "test", Details() with { PickupAddress = " " }, Now));
        Assert.Throws<ArgumentException>(() =>
            Parcel.ForCustomer(Guid.NewGuid(), Guid.NewGuid(), "test", Details() with { DropoffContact = " " }, Now));
    }

    [Fact]
    public void MerchantParcelRetainsStableOrderIdentityWithoutCustomerOwnership()
    {
        var merchant = Guid.NewGuid();
        var parcel = Parcel.ForMerchant(Guid.NewGuid(), merchant, "external-order-42", "test", Details(), Now);

        Assert.Equal(merchant, parcel.MerchantId);
        Assert.Equal("external-order-42", parcel.ExternalOrderId);
        Assert.Null(parcel.CustomerId);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Parcel.ForMerchant(Guid.NewGuid(), merchant, new string('a', 129), "test", Details(), Now));
    }

    [Fact]
    public void QuoteBindsOwnerAndTariffAndExpiresAfterTenMinutes()
    {
        var owner = Guid.NewGuid();
        var tier = Tier();
        var quote = new Quote(Guid.NewGuid(), owner, "fingerprint", "{\"recipient\":\"fictional\"}", tier, 540m, Now);

        Assert.Equal(owner, quote.OwnerId);
        Assert.Equal(tier.Id, quote.PricingTierId);
        Assert.Equal(tier.PricingVersion, quote.PricingVersion);
        Assert.Equal("LKR", quote.Currency);
        Assert.Equal(Now.AddMinutes(10), quote.ExpiresAt);
        Assert.Null(quote.ConsumedByParcelId);
    }

    [Fact]
    public void QuoteCannotBeConsumedByAnotherCustomerOrAfterExpiration()
    {
        var owner = Guid.NewGuid();
        var quote = new Quote(Guid.NewGuid(), owner, "fingerprint", "{}", Tier(), 540m, Now);
        var foreignParcel = Parcel.ForCustomer(Guid.NewGuid(), Guid.NewGuid(), "foreign", Details(), Now);
        var ownedParcel = Parcel.ForCustomer(Guid.NewGuid(), owner, "owned", Details(), Now);

        Assert.Throws<InvalidOperationException>(() => quote.Consume(foreignParcel, Now));
        Assert.Throws<InvalidOperationException>(() => quote.Consume(ownedParcel, quote.ExpiresAt));
        Assert.Null(quote.ConsumedByParcelId);
    }

    [Fact]
    public void QuoteCanBeConsumedOnceByItsCustomer()
    {
        var owner = Guid.NewGuid();
        var quote = new Quote(Guid.NewGuid(), owner, "fingerprint", "{}", Tier(), 540m, Now);
        var parcel = Parcel.ForCustomer(Guid.NewGuid(), owner, "first", Details(), Now);
        var other = Parcel.ForCustomer(Guid.NewGuid(), owner, "second", Details(), Now);

        quote.Consume(parcel, Now.AddMinutes(1));

        Assert.Equal(parcel.Id, quote.ConsumedByParcelId);
        Assert.Throws<InvalidOperationException>(() => quote.Consume(other, Now.AddMinutes(2)));
        Assert.Equal(parcel.Id, quote.ConsumedByParcelId);
    }

    [Fact]
    public void SnapshotRequiresMatchingComponentsAndCapturesAcceptedTariff()
    {
        var tier = Tier();
        var snapshot = new PriceSnapshot(Guid.NewGuid(), Guid.NewGuid(), tier, 450m, 90m, 540m, Now);

        Assert.Equal(540m, snapshot.Total);
        Assert.Equal(tier.PricingVersion, snapshot.PricingVersion);
        Assert.Equal(tier.Id, snapshot.PricingTierId);
        Assert.Throws<ArgumentException>(() =>
            new PriceSnapshot(Guid.NewGuid(), Guid.NewGuid(), tier, 450m, 90m, 541m, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PriceSnapshot(Guid.NewGuid(), Guid.NewGuid(), tier, -1m, 1m, 0m, Now));
    }

    [Fact]
    public void InitialHistoryAndOutboxDescribeCreationWithoutPrivateData()
    {
        var owner = Guid.NewGuid();
        var parcel = Parcel.ForCustomer(Guid.NewGuid(), owner, "tracking", Details(), Now);
        var history = BookingHistory.Created(Guid.NewGuid(), parcel, owner);
        var eventId = Guid.NewGuid();
        var outbox = OutboxMessage.ParcelCreated(eventId, parcel, "correlation-42");

        Assert.Null(history.FromStatus);
        Assert.Equal(ParcelStatus.Created, history.ToStatus);
        Assert.Equal(owner, history.ChangedBy);
        Assert.Equal(parcel.Id, history.ParcelId);
        Assert.Equal(Now, history.Timestamp);
        Assert.Null(outbox.PublishedAt);
        Assert.Equal(eventId, outbox.Id);
        using var document = JsonDocument.Parse(outbox.Payload);
        var payload = document.RootElement;
        Assert.Equal(10, payload.EnumerateObject().Count());
        Assert.Equal(eventId, payload.GetProperty("eventId").GetGuid());
        Assert.Equal("StatusChanged", payload.GetProperty("eventType").GetString());
        Assert.Equal(1, payload.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Now, payload.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(parcel.Id.ToString(), payload.GetProperty("parcelId").GetString());
        Assert.Equal(1, payload.GetProperty("parcelVersion").GetInt64());
        Assert.Equal("Created", payload.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("previousStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("merchantId").ValueKind);
        Assert.Equal("correlation-42", payload.GetProperty("correlationId").GetString());
        Assert.DoesNotContain(parcel.PickupAddress, outbox.Payload);
        Assert.DoesNotContain(parcel.DropoffContact, outbox.Payload);
    }

    [Fact]
    public void IdempotencyStoresExactResponseAndCannotOverwriteIt()
    {
        var result = new IdempotencyRecord(Guid.NewGuid(), "customer-42", "CreateParcel", "key", "fingerprint", Now);
        const string body = "{ \"id\": \"parcel-42\", \"total\": 540.00 }";
        var parcelId = Guid.NewGuid();

        result.Complete(parcelId, 201, body, "/api/v1/parcels/parcel-42");

        Assert.Equal(Now.AddDays(30), result.ExpiresAt);
        Assert.Equal(parcelId, result.ParcelId);
        using var stored = JsonDocument.Parse(result.StoredResponse!);
        Assert.Equal(201, stored.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(body, stored.RootElement.GetProperty("body").GetString());
        Assert.Equal("/api/v1/parcels/parcel-42", stored.RootElement.GetProperty("location").GetString());
        Assert.Throws<InvalidOperationException>(() => result.Complete(parcelId, 201, "{}", "/changed"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IdempotencyRecord(Guid.NewGuid(), "customer", "CreateParcel", new string('k', 129), "fingerprint", Now));
    }

    private static ParcelDetails Details() => new(
        ParcelSize.Small, 2.5m, Guid.NewGuid(), "Fictional pickup address", null,
        "Fictional destination address", "Fictional Recipient / 0000000000");

    private static PricingTier Tier() => new(Guid.NewGuid(), Guid.NewGuid(), ParcelSize.Small, "test-v1", 450m, 60m);
}
