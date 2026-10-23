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

    public Guid Id { get; private set; }
    public Guid ParcelId { get; private set; }
    public ParcelStatus? FromStatus { get; private set; }
    public ParcelStatus ToStatus { get; private set; }
    public Guid ChangedBy { get; private set; }
    public string? ChangeReason { get; private set; }
    public string? DeliveryNote { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
}
