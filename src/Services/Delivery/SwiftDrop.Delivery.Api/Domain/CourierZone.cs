namespace SwiftDrop.Delivery.Api.Domain;

public sealed class CourierZone
{
    private CourierZone() { }

    public CourierZone(Guid courierId, Guid zoneId)
    {
        CourierId = DomainGuard.Id(courierId, nameof(courierId));
        ZoneId = DomainGuard.Id(zoneId, nameof(zoneId));
    }

    public Guid CourierId { get; private set; }
    public Guid ZoneId { get; private set; }
}
