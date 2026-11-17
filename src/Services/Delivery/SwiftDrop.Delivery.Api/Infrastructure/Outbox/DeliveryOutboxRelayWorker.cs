using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Contracts.Events.V1;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.Outbox;

public sealed class DeliveryOutboxRelayWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DeliveryOutboxRelayWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DeliveryOutboxRelayWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedCount = await ProcessPendingBatchAsync(stoppingToken);
                if (processedCount == 0)
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
                logger.LogError(ex, "Error processing Delivery outbox messages.");
                await Task.Delay(PollingInterval, stoppingToken);
            }
        }
    }

    private async Task<int> ProcessPendingBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
        var publishEndpoint = scope.ServiceProvider.GetService<IPublishEndpoint>();
        var httpClientFactory = scope.ServiceProvider.GetService<IHttpClientFactory>();

        var pendingMessages = await database.OutboxMessages
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Take(25)
            .ToListAsync(cancellationToken);

        if (pendingMessages.Count == 0)
        {
            return 0;
        }

        var merchantBaseUrl = configuration["Merchant:ApiUrl"]
            ?? configuration["MerchantIntegration:Address"]
            ?? (configuration["DOTNET_RUNNING_IN_CONTAINER"] == "true" ? "http://merchant-service:8080" : "http://127.0.0.4:8080");

        var client = httpClientFactory?.CreateClient("MerchantInternal");

        foreach (var message in pendingMessages)
        {
            StatusChangedV1? statusEvent = null;
            try
            {
                statusEvent = JsonSerializer.Deserialize<StatusChangedV1>(message.Payload, JsonOptions);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to deserialize outbox message {MessageId} payload.", message.Id);
                message.MarkPublished(DateTimeOffset.UtcNow);
                continue;
            }

            if (statusEvent is null)
            {
                message.MarkPublished(DateTimeOffset.UtcNow);
                continue;
            }

            // 1. Publish to RabbitMQ MassTransit if endpoint is available
            if (publishEndpoint is not null)
            {
                try
                {
                    await publishEndpoint.Publish<StatusChangedV1>(statusEvent, cancellationToken);
                    logger.LogInformation("Published StatusChanged event {EventId} for parcel {ParcelId} ({Status}) to broker.",
                        statusEvent.EventId, statusEvent.ParcelId, statusEvent.Status);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Broker publication failed for event {EventId}; falling back to direct relay.", statusEvent.EventId);
                }
            }

            // 2. Direct relay to Merchant if parcel belongs to a merchant and broker was not used or direct relay is desired
            if (!string.IsNullOrWhiteSpace(statusEvent.MerchantId) && Guid.TryParse(statusEvent.MerchantId, out var merchantGuid) && client is not null)
            {
                try
                {
                    var payload = new
                    {
                        eventId = statusEvent.EventId,
                        eventType = statusEvent.EventType,
                        schemaVersion = statusEvent.SchemaVersion,
                        occurredAt = statusEvent.OccurredAt,
                        parcelId = Guid.Parse(statusEvent.ParcelId),
                        parcelVersion = (int)statusEvent.ParcelVersion,
                        merchantId = merchantGuid,
                        status = statusEvent.Status
                    };

                    using var response = await client.PostAsJsonAsync($"{merchantBaseUrl.TrimEnd('/')}/internal/v1/webhook-dispatches", payload, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        logger.LogInformation("Directly relayed event {EventId} to Merchant for parcel {ParcelId} ({Status}).",
                            statusEvent.EventId, statusEvent.ParcelId, statusEvent.Status);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Direct relay to Merchant for event {EventId} was not completed.", statusEvent.EventId);
                }
            }

            message.MarkPublished(DateTimeOffset.UtcNow);
        }

        await database.SaveChangesAsync(cancellationToken);
        return pendingMessages.Count;
    }
}
