namespace SwiftDrop.Delivery.Api.Application;

/// <summary>Identity established by a trusted entry adapter, never by quote input.</summary>
public interface ICurrentCustomer
{
    bool IsAuthenticated { get; }
    bool IsCustomer { get; }
    Guid? CustomerId { get; }
}
