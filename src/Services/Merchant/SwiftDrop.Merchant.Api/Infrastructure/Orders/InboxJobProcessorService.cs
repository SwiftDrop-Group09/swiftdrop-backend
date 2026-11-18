using Microsoft.Extensions.Options;
using SwiftDrop.Merchant.Api.Application.Orders.Processing;

namespace SwiftDrop.Merchant.Api.Infrastructure.Orders;

/// <summary>
/// Embedded order processor. Each tick runs in its own DI scope (fresh
/// DbContext). When a batch was full it immediately polls again; when nothing
/// was due it waits <see cref="OrderProcessingOptions.PollingInterval"/>.
/// An error in one tick is logged and never stops the loop.
/// </summary>
public sealed class InboxJobProcessorService(
    IServiceScopeFactory scopeFactory,
    IOptions<OrderProcessingOptions> options,
    ILogger<InboxJobProcessorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Merchant order processor is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<ProcessDueOrdersHandler>();
                var claimed = await handler.ProcessBatchAsync(stoppingToken);
                if (claimed < settings.BatchSize)
                {
                    await Task.Delay(settings.PollingInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Error processing merchant order jobs batch.");
                await Task.Delay(settings.PollingInterval, stoppingToken);
            }
        }
    }
}
