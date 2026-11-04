namespace SwiftDrop.Merchant.Api.Domain;

public sealed class Business
{
    private Business() { }

    private Business(
        Guid id,
        Guid userId,
        string businessName,
        string contactEmail,
        string contactPhone,
        string pickupAddress,
        DateTimeOffset registeredAt)
    {
        Id = DomainGuard.Id(id, nameof(id));
        UserId = DomainGuard.Id(userId, nameof(userId));
        BusinessName = DomainGuard.Text(businessName, nameof(businessName), 200);
        ContactEmail = DomainGuard.Email(contactEmail, nameof(contactEmail));
        ContactPhone = DomainGuard.Text(contactPhone, nameof(contactPhone), 32);
        PickupAddress = DomainGuard.Text(pickupAddress, nameof(pickupAddress), 500);
        Status = BusinessStatus.Pending;
        RegisteredAt = registeredAt.ToUniversalTime();
    }

    public static Business Register(
        Guid userId,
        string businessName,
        string contactEmail,
        string contactPhone,
        string pickupAddress,
        DateTimeOffset? registeredAt = null)
    {
        return new Business(
            Guid.NewGuid(),
            userId,
            businessName,
            contactEmail,
            contactPhone,
            pickupAddress,
            registeredAt ?? DateTimeOffset.UtcNow);
    }

    public void Approve(Guid adminUserId)
    {
        DomainGuard.Id(adminUserId, nameof(adminUserId));
        if (Status != BusinessStatus.Pending && Status != BusinessStatus.Suspended)
        {
            throw new InvalidOperationException($"Cannot approve business from status '{Status}'. Only Pending or Suspended can be approved.");
        }

        Status = BusinessStatus.Approved;
        ApprovedAt = DateTimeOffset.UtcNow;
        ApprovedBy = adminUserId;
        RejectionReason = null;
    }

    public void Reject(Guid adminUserId, string reason)
    {
        DomainGuard.Id(adminUserId, nameof(adminUserId));
        DomainGuard.Text(reason, nameof(reason), 500);

        if (Status != BusinessStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot reject business from status '{Status}'. Only Pending applications can be rejected.");
        }

        Status = BusinessStatus.Rejected;
        ApprovedAt = DateTimeOffset.UtcNow;
        ApprovedBy = adminUserId;
        RejectionReason = reason.Trim();
    }

    public void Suspend(Guid adminUserId, string reason)
    {
        DomainGuard.Id(adminUserId, nameof(adminUserId));
        DomainGuard.Text(reason, nameof(reason), 500);

        if (Status != BusinessStatus.Approved)
        {
            throw new InvalidOperationException($"Cannot suspend business from status '{Status}'. Only Approved businesses can be suspended.");
        }

        Status = BusinessStatus.Suspended;
        ApprovedBy = adminUserId;
        RejectionReason = reason.Trim();
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string BusinessName { get; private set; } = null!;
    public string ContactEmail { get; private set; } = null!;
    public string ContactPhone { get; private set; } = null!;
    public string PickupAddress { get; private set; } = null!;
    public BusinessStatus Status { get; private set; }
    public string? RejectionReason { get; private set; }
    public DateTimeOffset RegisteredAt { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public Guid? ApprovedBy { get; private set; }
}

