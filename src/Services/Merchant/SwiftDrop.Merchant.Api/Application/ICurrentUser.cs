namespace SwiftDrop.Merchant.Api.Application;

/// <summary>Identity established by a trusted entry adapter, never by request input.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    bool IsAdmin { get; }
    bool IsMerchant { get; }
    Guid? UserId { get; }
}
