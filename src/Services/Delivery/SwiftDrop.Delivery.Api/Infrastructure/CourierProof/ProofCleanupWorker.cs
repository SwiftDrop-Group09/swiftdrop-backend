using SwiftDrop.Delivery.Api.Application.CourierProof;

namespace SwiftDrop.Delivery.Api.Infrastructure.CourierProof;

public sealed class ProofCleanupWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ProofCleanupWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No configured storage means no network/DB startup dependency. Configured
        // deployments run at startup and daily; each pass fails closed as a unit.
        if (string.IsNullOrWhiteSpace(configuration["ProofStorage:Bucket"])) return;
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var removed = await scope.ServiceProvider.GetRequiredService<ProofCleanup>().RunOnceAsync(stoppingToken);
                logger.LogInformation("Proof orphan cleanup completed; removed {Count} objects.", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { logger.LogWarning("Proof orphan cleanup unavailable; no reference check is bypassed."); }
            try { await Task.Delay(TimeSpan.FromDays(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
