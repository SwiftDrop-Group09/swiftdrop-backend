namespace SwiftDrop.Delivery.Api.Domain;

public sealed class DeliveryZone
{
    private DeliveryZone() { }

    public DeliveryZone(Guid id, string name, string? description = null, string? boundaries = null)
    {
        Id = DomainGuard.Id(id, nameof(id));
        Name = DomainGuard.Text(name, nameof(name));
        Description = description;
        Boundaries = boundaries is null ? null : DomainGuard.JsonObject(boundaries, nameof(boundaries));
        IsActive = true;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public string? Boundaries { get; private set; }
    public bool IsActive { get; private set; }
}
