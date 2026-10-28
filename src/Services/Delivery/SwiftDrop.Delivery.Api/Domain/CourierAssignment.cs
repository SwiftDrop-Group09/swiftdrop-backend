namespace SwiftDrop.Delivery.Api.Domain;

/// <summary>A retained operational assignment, distinct from the parcel lifecycle.</summary>
/// <remarks>
/// These record operations do not authorize a caller or update a parcel. The owning
/// application transaction must verify current ownership/version, capacity and zone,
/// and persist parcel, assignment, history and outbox changes together.
/// </remarks>
public sealed class CourierAssignment
{
    private CourierAssignment() { }

    public CourierAssignment(Guid id, Guid parcelId, Guid courierId, DateOnly serviceDate,
        DateTimeOffset assignedAt)
    {
        Id = DomainGuard.Id(id, nameof(id));
        ParcelId = DomainGuard.Id(parcelId, nameof(parcelId));
        CourierId = DomainGuard.Id(courierId, nameof(courierId));
        ServiceDate = serviceDate;
        AssignedAt = assignedAt.ToUniversalTime();
        Status = AssignmentStatus.Active;
    }

    public Guid Id { get; private set; }
    public Guid ParcelId { get; private set; }
    public Guid CourierId { get; private set; }
    public DateOnly ServiceDate { get; private set; }
    public AssignmentStatus Status { get; private set; }
    public DateTimeOffset AssignedAt { get; private set; }
    public DateTimeOffset? PickedUpAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }

    public void MarkPickedUp(DateTimeOffset pickedUpAt)
    {
        RequireActive();
        if (PickedUpAt is not null)
        {
            throw new InvalidOperationException("Pickup has already been recorded.");
        }

        if (pickedUpAt < AssignedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(pickedUpAt));
        }

        PickedUpAt = pickedUpAt.ToUniversalTime();
    }

    public void MarkReassigned()
    {
        RequireBeforePickup();
        Status = AssignmentStatus.Reassigned;
    }

    public void MarkCancelled()
    {
        RequireBeforePickup();
        Status = AssignmentStatus.Cancelled;
    }

    public void MarkFailed()
    {
        RequireActive();
        Status = AssignmentStatus.Failed;
    }

    // Only the later authorized proof-completion handler may invoke this operation.
    public void MarkDelivered(DateTimeOffset deliveredAt)
    {
        RequireActive();
        if (PickedUpAt is null)
        {
            throw new InvalidOperationException("Pickup must be recorded before delivery.");
        }

        if (deliveredAt < PickedUpAt.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(deliveredAt));
        }

        DeliveredAt = deliveredAt.ToUniversalTime();
        Status = AssignmentStatus.Delivered;
    }

    private void RequireActive()
    {
        if (Status != AssignmentStatus.Active)
        {
            throw new InvalidOperationException("A closed assignment cannot be changed.");
        }
    }

    private void RequireBeforePickup()
    {
        RequireActive();
        if (PickedUpAt is not null)
        {
            throw new InvalidOperationException("Reassignment and cancellation are only allowed before pickup.");
        }
    }
}
