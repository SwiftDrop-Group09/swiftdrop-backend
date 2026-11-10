using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;

namespace SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Background;

public sealed class WebhookDispatchBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<WebhookDispatchBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private const int BatchSize = 25;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerToken = Guid.NewGuid();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<ProcessDueWebhookJobsHandler>();
                var processed = await handler.ProcessBatchAsync(BatchSize, LeaseDuration, workerToken, stoppingToken);

                if (processed == 0)
                {
                    await Task.Delay(PollingInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing webhook dispatch jobs batch.");
                await Task.Delay(PollingInterval, stoppingToken);
            }
        }
    }
}
