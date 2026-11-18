using MassTransit;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;
using SwiftDrop.Contracts.Events.V1;

namespace SwiftDrop.Delivery.OutboxRelay;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton(TimeProvider.System);

        var connectionString = builder.Configuration.GetConnectionString("DeliveryDatabase");
        var broker = builder.Configuration.GetSection("OutboxRelay:Broker");
        var host = broker["Host"];
        var username = broker["Username"];
        var password = broker["Password"];
        var virtualHost = broker["VirtualHost"] ?? "/";
        var enabled = !string.IsNullOrWhiteSpace(connectionString) &&
            !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(username) &&
            !string.IsNullOrWhiteSpace(password);

        if (enabled)
        {
            var database = RequireOutboxConnection(connectionString!);
            builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(database));
            builder.Services.AddSingleton(new RelaySettings(
                BatchSize: PositiveInt(builder.Configuration["OutboxRelay:BatchSize"], 20, 100),
                PollInterval: TimeSpan.FromMilliseconds(PositiveInt(
                    builder.Configuration["OutboxRelay:PollIntervalMilliseconds"], 1000, 60000))));
            builder.Services.AddMassTransit(registration =>
            {
                registration.UsingRabbitMq((_, cfg) =>
                {
                    cfg.Host(host!, virtualHost, settings =>
                    {
                        settings.Username(username!);
                        settings.Password(password!);
                    });
                    cfg.Message<StatusChangedV1>(message =>
                        message.SetEntityName("SwiftDrop.Contracts.V1:StatusChanged"));
                });
            });
            builder.Services.AddHostedService<OutboxRelayWorker>();
        }

        var app = builder.Build();
        app.MapHealthChecks("/health", new HealthCheckOptions());
        if (!enabled)
        {
            app.Logger.LogWarning(
                "Outbox relay is idle because its dedicated Delivery outbox connection or broker credentials are not configured.");
        }

        app.Run();
    }

    private static string RequireOutboxConnection(string connectionString)
    {
        NpgsqlConnectionStringBuilder settings;
        try
        {
            settings = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                "The outbox relay requires its dedicated connection to the swiftdrop_delivery database.");
        }

        if (!string.Equals(settings.Database, "swiftdrop_delivery", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The outbox relay requires its dedicated connection to the swiftdrop_delivery database.");
        }

        return settings.ConnectionString;
    }

    private static int PositiveInt(string? value, int fallback, int maximum) =>
        int.TryParse(value, out var parsed) && parsed is > 0 && parsed <= maximum ? parsed : fallback;
}

public sealed record RelaySettings(int BatchSize, TimeSpan PollInterval);
