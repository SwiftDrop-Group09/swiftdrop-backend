using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence;

public sealed class DeliveryDbContextFactory : IDesignTimeDbContextFactory<DeliveryDbContext>
{
    public DeliveryDbContext CreateDbContext(string[] args)
    {
        // Scaffolding/script generation needs a provider, not a connection or secret.
        // Database update explicitly requires the owning service connection setting.
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var connectionString = DeliveryPersistence.FindConnectionString(configuration);
        var options = new DbContextOptionsBuilder<DeliveryDbContext>();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            options.UseNpgsql();
        }
        else
        {
            options.UseNpgsql(DeliveryPersistence.RequireDeliveryDatabase(connectionString));
        }

        return new DeliveryDbContext(options.Options);
    }
}
