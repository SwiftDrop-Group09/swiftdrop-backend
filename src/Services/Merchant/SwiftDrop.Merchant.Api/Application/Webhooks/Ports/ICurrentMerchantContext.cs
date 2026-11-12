namespace SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

public interface ICurrentMerchantContext
{
    bool IsAuthenticated { get; }
    bool IsApprovedMerchant { get; }
    bool CanManageWebhooks { get; }
    Guid? BusinessId { get; }
    Guid? ActorId { get; }
}
