using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Tests.Courier;

public sealed class CourierProofDomainTests
{
    [Fact]
    public void MissingOrWrongParcelOrDuplicateKindCannotCompleteEvenInsideTheDomain()
    {
        using var context = new DeliveryDbContext(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql().Options);
        var now = DateTimeOffset.UtcNow;
        var parcel = Parcel.ForCustomer(Guid.NewGuid(), Guid.NewGuid(), "domain-proof-tracking",
            new(ParcelSize.Small, 1m, Guid.NewGuid(), "pickup", null, "dropoff", "contact"), now);
        context.Entry(parcel).Property(x => x.Status).CurrentValue = ParcelStatus.OutForDelivery;
        context.Entry(parcel).Property(x => x.Version).CurrentValue = 5;
        var photo = new ProofMetadata(Guid.NewGuid(), parcel.Id, ProofType.Photo, "opaque-private-ref", "Recipient", Guid.NewGuid(), now);
        Assert.Throws<ArgumentException>(() => parcel.CompleteWithProof(5, [], now));
        var foreign = new ProofMetadata(Guid.NewGuid(), Guid.NewGuid(), ProofType.Photo, "other-ref", "Recipient", Guid.NewGuid(), now);
        Assert.Throws<ArgumentException>(() => parcel.CompleteWithProof(5, [foreign], now));
        Assert.Throws<ArgumentException>(() => parcel.CompleteWithProof(5, [photo, photo], now));
        Assert.Equal(5, parcel.Version);
        Assert.Equal(ParcelStatus.OutForDelivery, parcel.Status);
        var stale = Assert.Throws<CourierTransitionException>(() => parcel.CompleteWithProof(4, [photo], now));
        Assert.Equal(CourierTransitionFailure.StaleVersion, stale.Failure);
        parcel.CompleteWithProof(5, [photo], now);
        Assert.Equal(ParcelStatus.Delivered, parcel.Status);
        Assert.Equal(6, parcel.Version);
        Assert.Throws<CourierTransitionException>(() => parcel.CompleteWithProof(6, [photo], now));
        Assert.False(CourierTransitionPolicy.IsAllowed(ParcelStatus.OutForDelivery, ParcelStatus.Delivered));
    }
}
