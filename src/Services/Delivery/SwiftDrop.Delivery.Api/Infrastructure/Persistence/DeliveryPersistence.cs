using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence;

public static class DeliveryPersistence
{
    public static IServiceCollection AddDeliveryPersistence(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<DeliveryDbContext>(options =>
        {
            var connectionString = FindConnectionString(configuration);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Delivery persistence requires ConnectionStrings:Database or DELIVERY_DATABASE_CONNECTION_STRING.");
            }

            options.UseNpgsql(RequireDeliveryDatabase(connectionString));
        });
        return services;
    }

    internal static string? FindConnectionString(IConfiguration configuration)
    {
        var scopedSetting = configuration.GetConnectionString("Database");
        return string.IsNullOrWhiteSpace(scopedSetting)
            ? configuration["DELIVERY_DATABASE_CONNECTION_STRING"] : scopedSetting;
    }

    public static string RequireDeliveryDatabase(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(settings.Database, "swiftdrop_delivery", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Delivery may connect only to its swiftdrop_delivery database.");
        }

        return settings.ConnectionString;
    }
}
