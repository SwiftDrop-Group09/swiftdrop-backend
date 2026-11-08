namespace SwiftDrop.Delivery.Api.Application;

/// <summary>Read access derived from a trusted authenticated principal, never request identifiers.</summary>
public interface ICurrentDeliveryUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    bool IsCourier { get; }
    bool IsCustomer { get; }
    bool IsOperations { get; }
}
