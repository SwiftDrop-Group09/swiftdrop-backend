namespace SwiftDrop.Delivery.Api.Domain;

public sealed class Parcel
{
    private Parcel() { }

    private Parcel(Guid id, Guid? customerId, Guid? merchantId, string? externalOrderId,
        string trackingNumber, ParcelDetails details, DateTimeOffset createdAt)
    {
        details.Validate();
        Id = DomainGuard.Id(id, nameof(id));
        CustomerId = customerId;
        MerchantId = merchantId;
        ExternalOrderId = externalOrderId;
        TrackingNumber = DomainGuard.Text(trackingNumber, nameof(trackingNumber));
        Size = details.Size;
        WeightKg = details.WeightKg;
        ZoneId = details.ZoneId;
        PickupAddress = details.PickupAddress;
        PickupContact = details.PickupContact;
        DropoffAddress = details.DropoffAddress;
        DropoffContact = details.DropoffContact;
        Status = ParcelStatus.Created;
        Version = 1;
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = CreatedAt;
    }

    public static Parcel ForCustomer(Guid id, Guid customerId, string trackingNumber,
        ParcelDetails details, DateTimeOffset createdAt) =>
        new(id, DomainGuard.Id(customerId, nameof(customerId)), null, null, trackingNumber, details, createdAt);

    public static Parcel ForMerchant(Guid id, Guid merchantId, string externalOrderId,
        string trackingNumber, ParcelDetails details, DateTimeOffset createdAt)
    {
        DomainGuard.Text(externalOrderId, nameof(externalOrderId));
        if (externalOrderId.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(externalOrderId));
        }

        return new(id, null, DomainGuard.Id(merchantId, nameof(merchantId)), externalOrderId,
            trackingNumber, details, createdAt);
    }

    public Guid Id { get; private set; }
    public Guid? CustomerId { get; private set; }
    public Guid? MerchantId { get; private set; }
    public string? ExternalOrderId { get; private set; }
    public string TrackingNumber { get; private set; } = null!;
    public long Version { get; private set; }
    public ParcelStatus Status { get; private set; }
    public ParcelSize Size { get; private set; }
    public decimal WeightKg { get; private set; }
    public string PickupAddress { get; private set; } = null!;
    public string? PickupContact { get; private set; }
    public string DropoffAddress { get; private set; } = null!;
    public string DropoffContact { get; private set; } = null!;
    public Guid ZoneId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // Authorization and assignment locking belong to the owning transaction.
    // Delivered is deliberately absent from the Courier status policy.
    public void AdvanceCourierStatus(ParcelStatus next, long expectedVersion, DateTimeOffset changedAt)
    {
        if (Version != expectedVersion)
            throw new CourierTransitionException(CourierTransitionFailure.StaleVersion, Version);
        if (!CourierTransitionPolicy.IsAllowed(Status, next))
            throw new CourierTransitionException(CourierTransitionFailure.InvalidTransition, Version);
        var nextVersion = checked(Version + 1);
        Status = next;
        Version = nextVersion;
        UpdatedAt = changedAt.ToUniversalTime();
    }

    public void CompleteWithProof(long expectedVersion, IReadOnlyList<ProofMetadata> proofs, DateTimeOffset changedAt)
    {
        if (Version != expectedVersion)
            throw new CourierTransitionException(CourierTransitionFailure.StaleVersion, Version);
        if (Status != ParcelStatus.OutForDelivery)
            throw new CourierTransitionException(CourierTransitionFailure.InvalidTransition, Version);
        ProofCompletionPolicy.Validate(proofs, Id);
        var nextVersion = checked(Version + 1);
        Status = ParcelStatus.Delivered;
        Version = nextVersion;
        UpdatedAt = changedAt.ToUniversalTime();
    }
}
