namespace SwiftDrop.Merchant.Api.Domain.Orders;

/// <summary>
/// Canonical parcel status names published by the Delivery service.
/// Merchant keeps them as text (it never references Delivery's types) but
/// only accepts known names, so dashboard counts stay meaningful.
/// </summary>
public static class ParcelStatuses
{
    public const string Created = "Created";
    public const string Assigned = "Assigned";
    public const string PickedUp = "PickedUp";
    public const string InTransit = "InTransit";
    public const string OutForDelivery = "OutForDelivery";
    public const string Delivered = "Delivered";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Created, Assigned, PickedUp, InTransit, OutForDelivery, Delivered, Failed, Cancelled
    };

    public static IReadOnlySet<string> Terminal { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Delivered, Failed, Cancelled
    };

    public static bool IsKnown(string? status) => status is not null && All.Contains(status);

    public static bool IsTerminal(string? status) => status is not null && Terminal.Contains(status);
}
