namespace SwiftDrop.Merchant.Api.Application.Orders.Processing;

/// <summary>Settings for the embedded order processor (section <c>Merchant:Orders:Processing</c>).</summary>
public sealed class OrderProcessingOptions
{
    public const string SectionName = "Merchant:Orders:Processing";

    /// <summary>Turns the background processor on or off (tests switch it off).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Wait between polls when no job was due.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int BatchSize { get; set; } = 20;

    /// <summary>How long a claimed job is reserved; must comfortably exceed two Delivery calls.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Deadline for each Delivery call (contract: five seconds).</summary>
    public TimeSpan DeliveryTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
