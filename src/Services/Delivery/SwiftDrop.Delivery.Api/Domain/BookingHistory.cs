namespace SwiftDrop.Delivery.Api.Domain;

public sealed class BookingHistory
{
    private BookingHistory() { }

    public static BookingHistory Created(Guid id, Parcel parcel, Guid changedBy) => new()
    {
        Id = DomainGuard.Id(id, nameof(id)),
        ParcelId = parcel.Id,
        FromStatus = null,
        ToStatus = ParcelStatus.Created,
        ChangedBy = DomainGuard.Id(changedBy, nameof(changedBy)),
        Timestamp = parcel.CreatedAt
    };

    public static BookingHistory Assigned(Guid id, Parcel parcel, Guid actor)
    {
        if (parcel.Status != ParcelStatus.Assigned || parcel.Version < 2)
            throw new InvalidOperationException("Assignment history requires an assigned parcel.");
        return new BookingHistory
        {
            Id = DomainGuard.Id(id, nameof(id)), ParcelId = parcel.Id,
            FromStatus = ParcelStatus.Created, ToStatus = ParcelStatus.Assigned,
            ChangedBy = DomainGuard.Id(actor, nameof(actor)), Timestamp = parcel.UpdatedAt
        };
    }

    public static BookingHistory Reassigned(Guid id, Parcel parcel, Guid actor, string reason)
    {
        if (parcel.Status != ParcelStatus.Assigned || parcel.Version < 3)
            throw new InvalidOperationException("Reassignment history requires a reassigned parcel.");
        reason = ReassignmentReason.Normalize(reason);
        return new BookingHistory
        {
            Id = DomainGuard.Id(id, nameof(id)), ParcelId = parcel.Id,
            FromStatus = ParcelStatus.Assigned, ToStatus = ParcelStatus.Assigned,
            ChangedBy = DomainGuard.Id(actor, nameof(actor)), Timestamp = parcel.UpdatedAt,
            ChangeReason = reason
        };
    }

    public static BookingHistory Unassigned(Guid id, Parcel parcel, Guid actor, string? reason)
    {
        if (parcel.Status != ParcelStatus.Created || parcel.Version < 2)
            throw new InvalidOperationException("Unassignment history requires a created parcel.");
        if (reason is not null) reason = ReassignmentReason.Normalize(reason);
        return new BookingHistory
        {
            Id = DomainGuard.Id(id, nameof(id)), ParcelId = parcel.Id,
            FromStatus = ParcelStatus.Assigned, ToStatus = ParcelStatus.Created,
            ChangedBy = DomainGuard.Id(actor, nameof(actor)), Timestamp = parcel.UpdatedAt,
            ChangeReason = reason
        };
    }

    public Guid Id { get; private set; }

    public static BookingHistory CourierChanged(Guid id, Parcel parcel, ParcelStatus previousStatus,
        Guid changedBy, string? notes, CourierFailureReason? reason)
    {
        if (!CourierTransitionPolicy.IsAllowed(previousStatus, parcel.Status) || parcel.Version < 2)
            throw new InvalidOperationException("History must describe an accepted Courier transition.");
        CourierTransitionPolicy.ValidateMetadata(parcel.Status, notes, reason);
        return new BookingHistory
        {
            Id = DomainGuard.Id(id, nameof(id)),
            ParcelId = parcel.Id,
            FromStatus = previousStatus,
            ToStatus = parcel.Status,
            ChangedBy = DomainGuard.Id(changedBy, nameof(changedBy)),
            DeliveryNote = notes,
            ChangeReason = reason?.ToString(),
            Timestamp = parcel.UpdatedAt
        };
    }
    public Guid ParcelId { get; private set; }

    public static BookingHistory ProofCompleted(Guid id, Parcel parcel, Guid actor)
    {
        if (parcel.Status != ParcelStatus.Delivered || parcel.Version < 2)
            throw new InvalidOperationException("Completion history requires Delivered.");
        return new BookingHistory
        {
            Id = DomainGuard.Id(id, nameof(id)), ParcelId = parcel.Id,
            FromStatus = ParcelStatus.OutForDelivery, ToStatus = ParcelStatus.Delivered,
            ChangedBy = DomainGuard.Id(actor, nameof(actor)), Timestamp = parcel.UpdatedAt
        };
    }
    public ParcelStatus? FromStatus { get; private set; }
    public ParcelStatus ToStatus { get; private set; }
    public Guid ChangedBy { get; private set; }
    public string? ChangeReason { get; private set; }
    public string? DeliveryNote { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
}
