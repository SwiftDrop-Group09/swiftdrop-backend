using Microsoft.Extensions.Options;
using SwiftDrop.Merchant.Api.Application.Orders.Processing;

namespace SwiftDrop.Merchant.Api.Infrastructure.Orders;

/// <summary>
/// Periodically refreshes parcel status for Created orders (see
/// <see cref="RefreshOrderStatusesHandler"/>). Shares the processor's
/// <see cref="OrderProcessingOptions.Enabled"/> switch.
/// </summary>
public sealed class OrderStatusRefreshService(
    IServiceScopeFactory scopeFactory,
    IOptions<OrderProcessingOptions> options,
    ILogger<OrderStatusRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<RefreshOrderStatusesHandler>();
                var checkedCount = await handler.RefreshBatchAsync(stoppingToken);
                if (checkedCount < settings.StatusRefreshBatchSize)
                {
                    await Task.Delay(settings.StatusRefreshPollingInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Error refreshing merchant order statuses.");
                await Task.Delay(settings.StatusRefreshPollingInterval, stoppingToken);
            }
        }
    }
}
