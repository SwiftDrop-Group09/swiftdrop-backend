using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Tests.Courier;

public sealed class CourierDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OperationalProfileRetainsIdentityIdWithoutAccountData()
    {
        var identityId = Guid.NewGuid();
        var courier = new Api.Domain.Courier(identityId, true, false, 10);

        Assert.Equal(identityId, courier.Id);
        Assert.True(courier.Active);
        Assert.False(courier.OnDuty);
        Assert.Equal(10, courier.Capacity);
        Assert.Equal(1, courier.Version);
        courier.Configure(false, true, 5);
        Assert.False(courier.Active);
        Assert.True(courier.OnDuty);
        Assert.Equal(5, courier.Capacity);
        Assert.Equal(2, courier.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidCapacityCannotPartiallyChangeProfile(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Api.Domain.Courier(Guid.NewGuid(), true, true, capacity));
        var courier = new Api.Domain.Courier(Guid.NewGuid(), true, true, 10);

        Assert.Throws<ArgumentOutOfRangeException>(() => courier.Configure(false, false, capacity));

        Assert.True(courier.Active);
        Assert.True(courier.OnDuty);
        Assert.Equal(10, courier.Capacity);
        Assert.Equal(1, courier.Version);
    }

    [Fact]
    public void ProfileAndZoneMembershipRequireNonemptyIds()
    {
        Assert.Throws<ArgumentException>(() => new Api.Domain.Courier(Guid.Empty, true, true, 10));
        Assert.Throws<ArgumentException>(() => new CourierZone(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new CourierZone(Guid.NewGuid(), Guid.Empty));
    }

    [Theory]
    [InlineData(ProofType.Photo)]
    [InlineData(ProofType.Signature)]
    public void ProofMetadataRetainsPrivateReferenceAndUtcActorTime(ProofType type)
    {
        var parcel = Parcel.ForCustomer(Guid.NewGuid(), Guid.NewGuid(), "fictional-tracking",
            new ParcelDetails(ParcelSize.Small, 1m, Guid.NewGuid(), "pickup", null, "dropoff", "recipient"), Now);
        var actor = Guid.NewGuid();
        var proof = new ProofMetadata(Guid.NewGuid(), parcel.Id, type, "proofs/generated-object-key",
            "Fictional Recipient", actor, Now.ToOffset(TimeSpan.FromHours(5.5)));

        Assert.Equal(parcel.Id, proof.ParcelId);
        Assert.Equal(type, proof.Type);
        Assert.Equal("proofs/generated-object-key", proof.StorageRef);
        Assert.Equal("Fictional Recipient", proof.RecipientName);
        Assert.Equal(actor, proof.UploadedBy);
        Assert.Equal(Now, proof.UploadedAt);
        Assert.Equal(TimeSpan.Zero, proof.UploadedAt.Offset);
        // Metadata alone must never advance status or imply a verified upload.
        Assert.Equal(ParcelStatus.Created, parcel.Status);
        Assert.Equal(1, parcel.Version);
    }

    [Theory]
    [InlineData("https://public.example.invalid/proof.jpg")]
    [InlineData("https://private.example.invalid/proof?signature=secret")]
    [InlineData("s3://bucket/key")]
    [InlineData("//public.example.invalid/proof.jpg")]
    [InlineData(" ")]
    public void ProofRejectsUrlsAndBlankReferences(string reference)
    {
        Assert.Throws<ArgumentException>(() => Proof(reference));
    }

    [Fact]
    public void ProofRejectsUnknownTypeMissingActorAndInvalidRecipient()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Proof(type: (ProofType)99));
        Assert.Throws<ArgumentException>(() => Proof(actor: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Proof(recipient: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => Proof(recipient: new string('a', 121)));
        Assert.Equal(120, Proof(recipient: new string('a', 120)).RecipientName.Length);
    }

    private static ProofMetadata Proof(string reference = "proofs/generated-key", ProofType type = ProofType.Photo,
        string recipient = "Fictional Recipient", Guid? actor = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), type, reference, recipient, actor ?? Guid.NewGuid(), Now);
}
