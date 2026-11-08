using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SwiftDrop.Merchant.Api.Infrastructure.Persistence;

public sealed class MerchantDbContextFactory : IDesignTimeDbContextFactory<MerchantDbContext>
{
    public MerchantDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = MerchantPersistence.FindMigrationConnectionString(configuration);
        var options = new DbContextOptionsBuilder<MerchantDbContext>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            options.UseNpgsql();
        }
        else
        {
            options.UseNpgsql(MerchantPersistence.RequireMerchantDatabase(connectionString));
        }

        return new MerchantDbContext(options.Options);
    }
}
