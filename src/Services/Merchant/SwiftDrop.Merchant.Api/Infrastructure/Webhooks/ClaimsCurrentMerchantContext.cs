using SwiftDrop.Merchant.Api.Application;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Api.Infrastructure.Webhooks;

public sealed class ClaimsCurrentMerchantContext(
    ICurrentUser currentUser,
    MerchantDbContext dbContext) : ICurrentMerchantContext
{
    private Business? _business;
    private bool _loaded;

    public bool IsAuthenticated => currentUser.IsAuthenticated && currentUser.UserId.HasValue;

    public Guid? ActorId => currentUser.UserId;

    public bool IsApprovedMerchant
    {
        get
        {
            EnsureLoaded();
            return _business is not null && _business.Status == BusinessStatus.Approved;
        }
    }

    public bool CanManageWebhooks => IsApprovedMerchant;

    public Guid? BusinessId
    {
        get
        {
            EnsureLoaded();
            return _business?.Id;
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (currentUser.UserId is { } userId && userId != Guid.Empty)
        {
            _business = dbContext.Businesses.FirstOrDefault(b => b.UserId == userId);
        }
    }
}
