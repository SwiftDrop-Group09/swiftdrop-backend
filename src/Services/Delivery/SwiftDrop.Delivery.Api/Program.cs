using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHealthChecks();
        builder.Services.AddDeliveryPersistence(builder.Configuration);

        // Explicit single-run migration task; normal host startup never applies DDL.
        if (args.Contains("--migrate", StringComparer.Ordinal))
        {
            using var migrationHost = builder.Build();
            using var scope = migrationHost.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
            database.Database.Migrate();
            return;
        }

        var app = builder.Build();

        // Process liveness only; dependency readiness is a later milestone.
        app.MapHealthChecks("/health");

        app.Run();
    }
}
