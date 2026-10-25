using System.Text.Json;

using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Tests.MerchantCompatibility;

public sealed class BookingCreationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Actor = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothOriginsUseServerTrackingAndTheSameCreatedPriceHistoryAndPrivateEventRules(bool merchant)
    {
        var (details, tier, price) = Input();
        var tracker = new Tracker();

        var write = Create(merchant, details, tier, price, tracker);

        Assert.Equal(1, tracker.Calls);
        Assert.Equal("TRK-test-server-generated", write.Parcel.TrackingNumber);
        Assert.NotEqual(Guid.Empty, write.Parcel.Id);
        Assert.Equal(merchant ? null : Owner, write.Parcel.CustomerId);
        Assert.Equal(merchant ? Owner : null, write.Parcel.MerchantId);
        Assert.Equal(merchant ? "fictional-order" : null, write.Parcel.ExternalOrderId);
        Assert.Equal(ParcelStatus.Created, write.Parcel.Status);
        Assert.Equal(1, write.Parcel.Version);
        Assert.Equal(Now, write.Parcel.CreatedAt);
        Assert.Equal(Now, write.Parcel.UpdatedAt);
        Assert.Equal(details.PickupAddress, write.Parcel.PickupAddress);
        Assert.Equal(details.DropoffAddress, write.Parcel.DropoffAddress);
        Assert.Equal(details.DropoffContact, write.Parcel.DropoffContact);
        Assert.Equal(details.WeightKg, write.Parcel.WeightKg);
        Assert.Equal(details.Size, write.Parcel.Size);
        Assert.Equal(details.ZoneId, write.Parcel.ZoneId);
        Assert.Equal(write.Parcel.Id, write.Snapshot.ParcelId);
        Assert.Equal(tier.Id, write.Snapshot.PricingTierId);
        Assert.Equal("validated-v1", write.Snapshot.PricingVersion);
        Assert.Equal("LKR", write.Snapshot.Currency);
        Assert.Equal(450m, write.Snapshot.BaseFee);
        Assert.Equal(90m, write.Snapshot.WeightFee);
        Assert.Equal(540m, write.Snapshot.Total);
        Assert.Equal(Now, write.Snapshot.CalculatedAt);
        Assert.Equal(write.Parcel.Id, write.History.ParcelId);
        Assert.Equal(merchant ? Actor : Owner, write.History.ChangedBy);
        Assert.Null(write.History.FromStatus);
        Assert.Equal(ParcelStatus.Created, write.History.ToStatus);
        Assert.Equal(Now, write.History.Timestamp);
        Assert.Null(write.Outbox.PublishedAt);
        using var document = JsonDocument.Parse(write.Outbox.Payload);
        var payload = document.RootElement;
        Assert.Equal(new[]
        {
            "correlationId", "eventId", "eventType", "merchantId", "occurredAt", "parcelId",
            "parcelVersion", "previousStatus", "schemaVersion", "status"
        }, payload.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(write.Outbox.Id, payload.GetProperty("eventId").GetGuid());
        Assert.Equal(write.Parcel.Id, payload.GetProperty("parcelId").GetGuid());
        Assert.Equal("StatusChanged", payload.GetProperty("eventType").GetString());
        Assert.Equal("Created", payload.GetProperty("status").GetString());
        Assert.Equal(1, payload.GetProperty("parcelVersion").GetInt64());
        Assert.Equal(1, payload.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Now, payload.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal("compatibility-test", payload.GetProperty("correlationId").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("previousStatus").ValueKind);
        if (merchant) Assert.Equal(Owner, payload.GetProperty("merchantId").GetGuid());
        else Assert.Equal(JsonValueKind.Null, payload.GetProperty("merchantId").ValueKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("validated-v2")]
    [InlineData("VALIDATED-V1")]
    [InlineData(" validated-v1 ")]
    public void MerchantRejectsAnotherAcceptedVersionBeforeGeneratingTracking(string? version)
    {
        var (details, tier, price) = Input();
        var tracker = new Tracker();

        Assert.Throws<ArgumentException>(() => BookingAssembler.CreateMerchant(Owner, "fictional-order", version!,
            details, tier, price, Actor, tracker, Now, "compatibility-test"));

        Assert.Equal(0, tracker.Calls);
    }

    [Theory]
    [InlineData(false, "weight-zero")]
    [InlineData(true, "weight-zero")]
    [InlineData(false, "weight-excess")]
    [InlineData(true, "weight-excess")]
    [InlineData(false, "size")]
    [InlineData(true, "size")]
    [InlineData(false, "zone")]
    [InlineData(true, "zone")]
    [InlineData(false, "pickup")]
    [InlineData(true, "pickup")]
    [InlineData(false, "dropoff")]
    [InlineData(true, "dropoff")]
    [InlineData(false, "recipient")]
    [InlineData(true, "recipient")]
    public void BothOriginsApplyTheSameParcelValidation(bool merchant, string invalid)
    {
        var (details, tier, price) = Input();
        details = invalid switch
        {
            "weight-zero" => details with { WeightKg = 0 },
            "weight-excess" => details with { WeightKg = 30.01m },
            "size" => details with { Size = (ParcelSize)99 },
            "zone" => details with { ZoneId = Guid.Empty },
            "pickup" => details with { PickupAddress = " " },
            "dropoff" => details with { DropoffAddress = " " },
            _ => details with { DropoffContact = " " }
        };

        Assert.ThrowsAny<ArgumentException>(() => Create(merchant, details, tier, price, new Tracker()));
    }

    [Theory]
    [InlineData(false, "zone")]
    [InlineData(true, "zone")]
    [InlineData(false, "size")]
    [InlineData(true, "size")]
    [InlineData(false, "price")]
    [InlineData(true, "price")]
    public void BothOriginsRejectTierOrPriceThatDoesNotMatchTheParcel(bool merchant, string mismatch)
    {
        var (details, tier, price) = Input();
        if (mismatch == "zone") details = details with { ZoneId = Guid.NewGuid() };
        if (mismatch == "size") details = details with { Size = ParcelSize.Large };
        if (mismatch == "price") price = price with { WeightFee = 91m, Total = 541m };

        Assert.Throws<ArgumentException>(() => Create(merchant, details, tier, price, new Tracker()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("too-long")]
    public void MerchantRequiresAStableNonblankOrderWithinTheExistingLimit(string? externalOrder)
    {
        var (details, tier, price) = Input();
        if (externalOrder == "too-long") externalOrder = new string('x', 129);

        Assert.ThrowsAny<ArgumentException>(() => BookingAssembler.CreateMerchant(Owner, externalOrder!,
            tier.PricingVersion, details, tier, price, Actor, new Tracker(), Now, "compatibility-test"));
    }

    [Fact]
    public void MerchantPreservesTheOpaqueExternalOrderAndUsesItsExplicitTrustedAuditActor()
    {
        var (details, tier, price) = Input();

        var write = BookingAssembler.CreateMerchant(Owner, " order / 001 ", tier.PricingVersion,
            details, tier, price, Actor, new Tracker(), Now, "compatibility-test");

        Assert.Equal(" order / 001 ", write.Parcel.ExternalOrderId);
        Assert.Equal(Actor, write.History.ChangedBy);
        Assert.NotEqual(write.Parcel.MerchantId, write.History.ChangedBy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothOriginsRequireANonemptyOwner(bool merchant)
    {
        var (details, tier, price) = Input();

        Assert.Throws<ArgumentException>(() => merchant
            ? BookingAssembler.CreateMerchant(Guid.Empty, "fictional-order", tier.PricingVersion,
                details, tier, price, Actor, new Tracker(), Now, "compatibility-test")
            : BookingAssembler.CreateCustomer(Guid.Empty, details, tier, price, new Tracker(), Now, "compatibility-test"));
    }

    [Fact]
    public void MerchantRequiresAnExplicitNonemptyAuditActor()
    {
        var (details, tier, price) = Input();

        Assert.Throws<ArgumentException>(() => BookingAssembler.CreateMerchant(Owner, "fictional-order",
            tier.PricingVersion, details, tier, price, Guid.Empty, new Tracker(), Now, "compatibility-test"));
    }

    private static BookingWriteSet Create(bool merchant, ParcelDetails details, PricingTier tier,
        PriceBreakdown price, Tracker tracker) => merchant
        ? BookingAssembler.CreateMerchant(Owner, "fictional-order", tier.PricingVersion,
            details, tier, price, Actor, tracker, Now, "compatibility-test")
        : BookingAssembler.CreateCustomer(Owner, details, tier, price, tracker, Now, "compatibility-test");

    private static (ParcelDetails Details, PricingTier Tier, PriceBreakdown Price) Input()
    {
        var zone = Guid.NewGuid();
        var details = new ParcelDetails(ParcelSize.Small, 2.5m, zone, "Fictional pickup", null,
            "Fictional destination", "Fictional Recipient / 0000000000");
        return (details, new PricingTier(Guid.NewGuid(), zone, ParcelSize.Small, "validated-v1", 450m, 60m),
            new PriceBreakdown(450m, 90m, 540m));
    }

    private sealed class Tracker : ITrackingNumberGenerator
    {
        public int Calls { get; private set; }
        public string Generate()
        {
            Calls++;
            return "TRK-test-server-generated";
        }
    }
}
