using MassTransit;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using SwiftDrop.Contracts.Events.V1;
using SwiftDrop.Notification.Worker.Messaging;
using SwiftDrop.Notification.Worker.Persistence;
using SwiftDrop.Notification.Worker.Routing;

namespace SwiftDrop.Notification.Worker;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton(TimeProvider.System);

        var database = builder.Configuration.GetConnectionString("Database");
        var migrationMode = args.Contains("--migrate", StringComparer.Ordinal);
        var broker = builder.Configuration.GetSection("Notification:Broker");
        if (!broker.Exists())
        {
            // The Kubernetes baseline uses the shorter service-local "Broker" section.
            broker = builder.Configuration.GetSection("Broker");
        }
        var brokerHost = broker["Host"];
        var brokerUser = broker["Username"];
        var brokerPassword = broker["Password"];
        var brokerVirtualHost = broker["VirtualHost"] ?? "/";
        var messagingEnabled = !string.IsNullOrWhiteSpace(database) &&
            !string.IsNullOrWhiteSpace(brokerHost) &&
            !string.IsNullOrWhiteSpace(brokerUser) &&
            !string.IsNullOrWhiteSpace(brokerPassword);

        if (!string.IsNullOrWhiteSpace(database))
        {
            builder.Services.AddDbContext<NotificationDbContext>(options => options.UseNpgsql(database));
            builder.Services.AddSingleton<ServiceJwtFileReader>();
            builder.Services.AddHttpClient("DeliveryInternal", client =>
                {
                    client.BaseAddress = ServiceAddress(builder.Configuration,
                        "Notification:ServiceEndpoints:Delivery", "http://delivery-service:8080/");
                    client.Timeout = TimeSpan.FromSeconds(5);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false
                });
            builder.Services.AddHttpClient("MerchantInternal", client =>
                {
                    client.BaseAddress = ServiceAddress(builder.Configuration,
                        "Notification:ServiceEndpoints:Merchant", "http://merchant-service:8080/");
                    client.Timeout = TimeSpan.FromSeconds(5);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false
                });
            builder.Services.AddSingleton<INotificationProvider, MockSmsNotificationProvider>();
            builder.Services.AddSingleton<NotificationProviderJobProcessor>();
            builder.Services.AddHostedService<StatusEffectRoutingWorker>();
        }

        if (messagingEnabled)
        {
            builder.Services.AddScoped<StatusNotificationProcessor>();
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
                        endpoint.PrefetchCount = 16;
                        endpoint.ConcurrentMessageLimit = 8;
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

        if (migrationMode)
        {
            if (string.IsNullOrWhiteSpace(database))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:Database must be configured to apply Notification Worker migrations.");
            }

            await using var migrationScope = app.Services.CreateAsyncScope();
            var migrationDb = migrationScope.ServiceProvider.GetRequiredService<NotificationDbContext>();
            await migrationDb.Database.MigrateAsync();
            return;
        }

        // Process liveness only; dependency readiness is a later milestone.
        app.MapHealthChecks("/health");

        if (messagingEnabled)
        {
            app.Logger.LogInformation("StatusChanged consumer is configured on queue notification-status.");
        }
        else
        {
            app.Logger.LogWarning(
                "Notification event consumption is disabled because the Notifications database or dedicated broker credentials are not configured.");
        }

        app.Run();
    }

    private static Uri ServiceAddress(IConfiguration configuration, string key, string fallback)
    {
        var configured = configuration[key] ?? fallback;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var address) ||
            address.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(address.UserInfo) ||
            !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
        {
            throw new InvalidOperationException($"{key} must be an absolute HTTP service URL without credentials or query parameters.");
        }

        return address;
    }
}
