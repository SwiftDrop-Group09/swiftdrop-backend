using MassTransit;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using SwiftDrop.Contracts.Events.V1;
using SwiftDrop.Notification.Worker.Messaging;
using SwiftDrop.Notification.Worker.Persistence;

namespace SwiftDrop.Notification.Worker;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton(TimeProvider.System);

        var database = builder.Configuration.GetConnectionString("Database");
        var broker = builder.Configuration.GetSection("Notification:Broker");
        var brokerHost = broker["Host"];
        var brokerUser = broker["Username"];
        var brokerPassword = broker["Password"];
        var brokerVirtualHost = broker["VirtualHost"] ?? "/";
        var messagingEnabled = !string.IsNullOrWhiteSpace(database) &&
            !string.IsNullOrWhiteSpace(brokerHost) &&
            !string.IsNullOrWhiteSpace(brokerUser) &&
            !string.IsNullOrWhiteSpace(brokerPassword);

        if (messagingEnabled)
        {
            builder.Services.AddDbContext<NotificationDbContext>(options => options.UseNpgsql(database));
            builder.Services.AddMassTransit(registration =>
            {
                registration.AddConsumer<StatusNotificationConsumer>();
                registration.UsingRabbitMq((context, cfg) =>
                {
                    cfg.Host(brokerHost!, brokerVirtualHost, host =>
                    {
                        host.Username(brokerUser!);
                        host.Password(brokerPassword!);
                    });
                    cfg.Message<StatusChangedV1>(message =>
                        message.SetEntityName("SwiftDrop.Contracts.V1:StatusChanged"));
                    cfg.ReceiveEndpoint("notification-status", endpoint =>
                    {
                        endpoint.ConfigureConsumeTopology = false;
                        endpoint.PublishFaults = false;
                        endpoint.Bind("SwiftDrop.Contracts.V1:StatusChanged", binding =>
                            binding.ExchangeType = ExchangeType.Fanout);
                        endpoint.UseMessageRetry(retry => retry.Intervals(
                            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));
                        endpoint.ConfigureConsumer<StatusNotificationConsumer>(context);
                    });
                });
            });
        }

        var app = builder.Build();

        // Process liveness only; dependency readiness is a later milestone.
        app.MapHealthChecks("/health");

        if (messagingEnabled)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
            await db.Database.MigrateAsync();
        }
        else
        {
            app.Logger.LogWarning(
                "Notification event consumption is disabled because the Notifications database or dedicated broker credentials are not configured.");
        }

        app.Run();
    }
}
