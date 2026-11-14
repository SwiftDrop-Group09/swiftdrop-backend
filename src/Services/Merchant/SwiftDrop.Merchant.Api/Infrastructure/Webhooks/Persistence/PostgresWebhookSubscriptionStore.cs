using Microsoft.EntityFrameworkCore;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Domain.Webhooks;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Persistence;

public sealed class PostgresWebhookSubscriptionStore(MerchantDbContext dbContext) : IWebhookSubscriptionStore
{
    public async Task<WebhookSubscription?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return await dbContext.WebhookSubscriptions
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<IReadOnlyList<WebhookSubscription>> ListByBusinessIdAsync(Guid businessId, CancellationToken ct)
    {
        return await dbContext.WebhookSubscriptions
            .AsNoTracking()
            .Where(s => s.BusinessId == businessId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WebhookSubscription>> ListActiveByBusinessAndEventAsync(Guid businessId, string eventName, CancellationToken ct)
    {
        var active = await dbContext.WebhookSubscriptions
            .AsNoTracking()
            .Where(s => s.BusinessId == businessId && s.IsActive)
            .ToListAsync(ct);

        return active.Where(s => s.IsSubscribedTo(eventName)).ToList();
    }

    public async Task AddAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        dbContext.WebhookSubscriptions.Add(subscription);
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        dbContext.WebhookSubscriptions.Update(subscription);
        await dbContext.SaveChangesAsync(ct);
    }
}
