using Microsoft.EntityFrameworkCore;
using SwiftDrop.Merchant.Api.Domain;

namespace SwiftDrop.Merchant.Api.Infrastructure.Persistence;

public sealed class MerchantDbContext(DbContextOptions<MerchantDbContext> options) : DbContext(options)
{
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        MerchantEntityConfigurations.Configure(modelBuilder);
}
