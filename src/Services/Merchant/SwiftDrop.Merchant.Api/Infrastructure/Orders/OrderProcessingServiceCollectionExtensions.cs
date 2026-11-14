using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Application.Orders.Processing;
using SwiftDrop.Merchant.Api.Infrastructure.Orders.Delivery;
using SwiftDrop.Merchant.Api.Infrastructure.Orders.Persistence;

namespace SwiftDrop.Merchant.Api.Infrastructure.Orders;

/// <summary>Which <see cref="IDeliveryBookingClient"/> to use (setting <c>Merchant:Orders:Delivery:Client</c>).</summary>
public enum DeliveryClientMode
{
    /// <summary>Default: orders wait in Retrying until a real client exists.</summary>
    Unconfigured,

    /// <summary>In-memory stand-in; allowed only in Development and Testing.</summary>
    Fake
}

public static class OrderProcessingServiceCollectionExtensions
{
    public const string DeliveryClientSetting = "Merchant:Orders:Delivery:Client";

    /// <summary>Registers the inbox queue, processor, Delivery client and background service.</summary>
    public static IServiceCollection AddMerchantOrderProcessing(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<OrderProcessingOptions>()
            .Bind(configuration.GetSection(OrderProcessingOptions.SectionName))
            .Validate(options => options.BatchSize > 0
                && options.PollingInterval > TimeSpan.Zero
                && options.DeliveryTimeout > TimeSpan.Zero
                && options.LeaseDuration > options.DeliveryTimeout * 2,
                "Order processing needs a positive batch size, polling interval and timeout, and a lease longer than two Delivery calls.")
            .Validate(options => options.StatusRefreshBatchSize > 0
                && options.StatusRefreshAfter > TimeSpan.Zero
                && options.StatusRefreshPollingInterval > TimeSpan.Zero,
                "Status refresh needs a positive batch size, staleness window and polling interval.")
            .ValidateOnStart();

        services.AddScoped<IInboxJobQueue, EfInboxJobQueue>();
        services.AddScoped<InboxJobProcessor>();
        services.AddScoped<ProcessDueOrdersHandler>();
        services.AddScoped<RefreshOrderStatusesHandler>();
        services.AddDeliveryBookingClient(configuration, environment);
        services.AddHostedService<InboxJobProcessorService>();
        services.AddHostedService<OrderStatusRefreshService>();
        return services;
    }

    private static void AddDeliveryBookingClient(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var mode = configuration.GetValue(DeliveryClientSetting, DeliveryClientMode.Unconfigured);
        switch (mode)
        {
            case DeliveryClientMode.Fake when environment.IsDevelopment() || environment.IsEnvironment("Testing"):
                services.AddSingleton<IDeliveryBookingClient, FakeDeliveryBookingClient>();
                break;

            case DeliveryClientMode.Fake:
                // The team's rule: no success stubs outside development. Fail at startup, loudly.
                throw new InvalidOperationException(
                    $"{DeliveryClientSetting}=Fake is only allowed in Development or Testing, not '{environment.EnvironmentName}'.");

            default:
                services.AddSingleton<IDeliveryBookingClient, UnconfiguredDeliveryBookingClient>();
                break;
        }
    }
}
