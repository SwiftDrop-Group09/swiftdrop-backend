namespace SwiftDrop.Delivery.Api.Domain;

/// <summary>Delivery's operational profile for an independently verified Identity user.</summary>
public sealed class Courier
{
    private Courier() { }

    public Courier(Guid identityUserId, bool active, bool onDuty, int capacity)
    {
        Id = DomainGuard.Id(identityUserId, nameof(identityUserId));
        ValidateCapacity(capacity);
        Active = active;
        OnDuty = onDuty;
        Capacity = capacity;
        Version = 1;
    }

    public Guid Id { get; private set; }
    public bool Active { get; private set; }
    public bool OnDuty { get; private set; }
    public int Capacity { get; private set; }
    public long Version { get; private set; }

    // The calling operational adapter must authorize and compare the expected version.
    public void Configure(bool active, bool onDuty, int capacity)
    {
        ValidateCapacity(capacity);
        var nextVersion = checked(Version + 1);
        Active = active;
        OnDuty = onDuty;
        Capacity = capacity;
        Version = nextVersion;
    }

    private static void ValidateCapacity(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }
    }
}
