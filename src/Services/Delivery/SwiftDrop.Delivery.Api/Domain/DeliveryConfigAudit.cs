namespace SwiftDrop.Delivery.Api.Domain;

public sealed class DeliveryConfigAudit
{
    private DeliveryConfigAudit() { }

    public DeliveryConfigAudit(Guid id, Guid actorId, string action, string resourceType,
        Guid resourceId, string? details, DateTimeOffset timestamp)
    {
        Id = DomainGuard.Id(id, nameof(id));
        ActorId = DomainGuard.Id(actorId, nameof(actorId));
        Action = DomainGuard.Text(action, nameof(action));
        ResourceType = DomainGuard.Text(resourceType, nameof(resourceType));
        ResourceId = DomainGuard.Id(resourceId, nameof(resourceId));
        Details = details;
        Timestamp = timestamp;
    }

    public Guid Id { get; private set; }
    public Guid ActorId { get; private set; }
    public string Action { get; private set; } = null!;
    public string ResourceType { get; private set; } = null!;
    public Guid ResourceId { get; private set; }
    public string? Details { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
}

