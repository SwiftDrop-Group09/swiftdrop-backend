namespace SwiftDrop.Delivery.Api.Domain;

public enum CourierFailureReason { RecipientUnavailable, AddressUnreachable, Damaged, Other }
public enum CourierTransitionFailure { StaleVersion, InvalidTransition }

public sealed class CourierTransitionException(CourierTransitionFailure failure, long currentVersion)
    : InvalidOperationException("The Courier transition cannot be applied.")
{
    public CourierTransitionFailure Failure { get; } = failure;
    public long CurrentVersion { get; } = currentVersion;
}

public static class CourierTransitionPolicy
{
    public static bool IsAllowed(ParcelStatus from, ParcelStatus to) => (from, to) switch
    {
        (ParcelStatus.Assigned, ParcelStatus.PickedUp) => true,
        (ParcelStatus.PickedUp, ParcelStatus.InTransit) => true,
        (ParcelStatus.InTransit, ParcelStatus.OutForDelivery) => true,
        (ParcelStatus.Assigned or ParcelStatus.PickedUp or ParcelStatus.InTransit or ParcelStatus.OutForDelivery,
            ParcelStatus.Failed) => true,
        _ => false
    };

    public static void ValidateMetadata(ParcelStatus to, string? notes, CourierFailureReason? reason)
    {
        if (notes is { Length: > 500 }) throw new ArgumentOutOfRangeException(nameof(notes));
        if (to is ParcelStatus.PickedUp or ParcelStatus.InTransit or ParcelStatus.OutForDelivery)
            DomainGuard.Text(notes!, nameof(notes));
        if (to == ParcelStatus.Failed && (reason is null || !Enum.IsDefined(reason.Value)))
            throw new ArgumentException("A documented delivery failure reason is required.", nameof(reason));
        if (to != ParcelStatus.Failed && reason is not null)
            throw new ArgumentException("A failure reason only belongs to Failed.", nameof(reason));
    }
}
