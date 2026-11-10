using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SwiftDrop.Merchant.Api.Infrastructure.Persistence;

public static class MerchantPersistence
{
    public static IServiceCollection AddMerchantPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<MerchantDbContext>(options =>
        {
            var connectionString = FindConnectionString(configuration);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Merchant persistence requires ConnectionStrings:Database or MERCHANT_DATABASE_CONNECTION_STRING.");
            }

            options.UseNpgsql(RequireMerchantDatabase(connectionString));
        });

        services.AddScoped<Application.Webhooks.Ports.IWebhookSubscriptionStore, Webhooks.Persistence.PostgresWebhookSubscriptionStore>();
        services.AddScoped<Application.Webhooks.Ports.IWebhookDispatchStore, Webhooks.Persistence.PostgresWebhookDispatchStore>();

        return services;
    }

    public static string? FindConnectionString(IConfiguration configuration)
    {
        var scopedSetting = configuration.GetConnectionString("Database");
        return string.IsNullOrWhiteSpace(scopedSetting)
            ? configuration["MERCHANT_DATABASE_CONNECTION_STRING"]
            : scopedSetting;
    }

    public static string? FindMigrationConnectionString(IConfiguration configuration)
    {
        var migrationSetting = configuration.GetConnectionString("Migration");
        if (!string.IsNullOrWhiteSpace(migrationSetting))
        {
            return migrationSetting;
        }

        var migrationEnv = configuration["MERCHANT_MIGRATION_CONNECTION_STRING"];
        return !string.IsNullOrWhiteSpace(migrationEnv)
            ? migrationEnv
            : FindConnectionString(configuration);
    }

    public static string RequireMerchantDatabase(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(settings.Database, "swiftdrop_merchant", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Merchant may connect only to its swiftdrop_merchant database.");
        }

        return settings.ConnectionString;
    }
}
