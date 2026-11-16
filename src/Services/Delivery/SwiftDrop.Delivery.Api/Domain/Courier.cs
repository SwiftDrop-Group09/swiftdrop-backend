namespace SwiftDrop.Delivery.Api.Domain;

/// <summary>Delivery's operational profile for an independently verified Identity user.</summary>
public sealed class Courier
{
    private Courier() { }

    public Courier(Guid identityUserId, bool active, bool onDuty, int capacity)
        : this(identityUserId, active, onDuty, capacity, VehicleType.Motorcycle, null, 30.00m)
    {
    }

    public Courier(Guid identityUserId, bool active, bool onDuty, int capacity,
        VehicleType vehicleType, string? vehicleNumber, decimal maxWeightKg)
    {
        Id = DomainGuard.Id(identityUserId, nameof(identityUserId));
        ValidateCapacity(capacity);
        ValidateMaxWeight(maxWeightKg);
        Active = active;
        OnDuty = onDuty;
        Capacity = capacity;
        VehicleType = vehicleType;
        VehicleNumber = CleanVehicleNumber(vehicleNumber);
        MaxWeightKg = maxWeightKg;
        Version = 1;
    }

    public Guid Id { get; private set; }
    public long BusinessNumber { get; private set; }
    public bool Active { get; private set; }
    public bool OnDuty { get; private set; }
    public int Capacity { get; private set; }
    public VehicleType VehicleType { get; private set; } = VehicleType.Motorcycle;
    public string? VehicleNumber { get; private set; }
    public decimal MaxWeightKg { get; private set; } = 30.00m;
    public long Version { get; private set; }

    public void Configure(bool active, bool onDuty, int capacity,
        VehicleType vehicleType = VehicleType.Motorcycle, string? vehicleNumber = null, decimal maxWeightKg = 30.00m)
    {
        ValidateCapacity(capacity);
        ValidateMaxWeight(maxWeightKg);
        var nextVersion = checked(Version + 1);
        Active = active;
        OnDuty = onDuty;
        Capacity = capacity;
        VehicleType = vehicleType;
        VehicleNumber = CleanVehicleNumber(vehicleNumber);
        MaxWeightKg = maxWeightKg;
        Version = nextVersion;
    }

    public void SetDuty(bool onDuty)
    {
        var nextVersion = checked(Version + 1);
        OnDuty = onDuty;
        Version = nextVersion;
    }

    private static void ValidateCapacity(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }
    }

    private static void ValidateMaxWeight(decimal maxWeightKg)
    {
        if (maxWeightKg <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWeightKg), "Max weight must be greater than zero.");
        }
    }

    private static string? CleanVehicleNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;
        var trimmed = number.Trim();
        return trimmed.Length > 32 ? trimmed[..32] : trimmed;
    }
}
