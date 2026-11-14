using Microsoft.EntityFrameworkCore;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Api.Domain.Webhooks;

namespace SwiftDrop.Merchant.Api.Infrastructure.Persistence;

public sealed class MerchantDbContext(DbContextOptions<MerchantDbContext> options) : DbContext(options)
{
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDeliveryJob> WebhookDispatchJobs => Set<WebhookDeliveryJob>();
    public DbSet<WebhookAttempt> WebhookAttempts => Set<WebhookAttempt>();
    public DbSet<WebhookReplayAudit> WebhookReplayAudits => Set<WebhookReplayAudit>();
    public DbSet<InboxJob> InboxJobs => Set<InboxJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        MerchantEntityConfigurations.Configure(modelBuilder);
}
