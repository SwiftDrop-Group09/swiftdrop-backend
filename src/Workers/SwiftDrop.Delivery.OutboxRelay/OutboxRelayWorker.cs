using System.Data;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;
using SwiftDrop.Contracts.Events.V1;

namespace SwiftDrop.Delivery.OutboxRelay;

public sealed class OutboxRelayWorker(
    NpgsqlDataSource database,
    IServiceScopeFactory scopes,
    RelaySettings settings,
    TimeProvider clock,
    ILogger<OutboxRelayWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await PublishBatchAsync(stoppingToken);
                if (count == 0)
                {
                    await Task.Delay(settings.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Delivery outbox relay pass failed; unpublished rows remain available for retry.");
                await Task.Delay(ErrorDelay, stoppingToken);
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var pending = new List<PendingOutboxMessage>(settings.BatchSize);
        await using (var select = new NpgsqlCommand("""
            SELECT id, event_type, payload
            FROM public.outbox_messages
            WHERE published_at IS NULL
            ORDER BY created_at, id
            LIMIT @batch_size
            FOR UPDATE SKIP LOCKED
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, settings.BatchSize);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                pending.Add(new PendingOutboxMessage(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        if (pending.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        await using var scope = scopes.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        foreach (var row in pending)
        {
            var message = OutboxEventParser.Parse(row.Id, row.EventType, row.Payload);
            await publisher.Publish(message, context => context.MessageId = message.EventId, cancellationToken);

            await using var update = new NpgsqlCommand("""
                UPDATE public.outbox_messages
                SET published_at = @published_at
                WHERE id = @id AND published_at IS NULL
                """, connection, transaction);
            update.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, row.Id);
            update.Parameters.AddWithValue("published_at", NpgsqlDbType.TimestampTz,
                clock.GetUtcNow().ToUniversalTime());
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("A locked Delivery outbox row could not be marked published.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Published {Count} Delivery status events to RabbitMQ.", pending.Count);
        return pending.Count;
    }

    private sealed record PendingOutboxMessage(Guid Id, string EventType, string Payload);
}
