using System.Text.Json;

namespace SwiftDrop.Delivery.Api.Presentation;

internal static class TrackingEventStream
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(30);

    internal sealed record Snapshot(long Version, object Payload, string? EventId = null, bool IsTerminal = false);

    public static IResult Create(HttpContext context, Snapshot initial,
        Func<CancellationToken, Task<Snapshot?>> readCurrent, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "private, no-store, no-cache, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        return Results.Stream(async stream =>
        {
            await using var writer = new StreamWriter(stream, leaveOpen: true);
            try
            {
                await WriteSnapshotAsync(writer, initial, ct);
                var lastVersion = initial.Version;
                var lastSnapshot = TimeProvider.System.GetTimestamp();
                var lastHeartbeat = lastSnapshot;
                var terminal = initial.IsTerminal;

                while (!terminal)
                {
                    await Task.Delay(PollInterval, ct);
                    if (await readCurrent(ct) is not { } current)
                    {
                        break;
                    }

                    var now = TimeProvider.System.GetTimestamp();
                    terminal = current.IsTerminal;
                    if (terminal || current.Version > lastVersion ||
                        TimeProvider.System.GetElapsedTime(lastSnapshot, now) >= ReconciliationInterval)
                    {
                        await WriteSnapshotAsync(writer, current, ct);
                        lastVersion = Math.Max(lastVersion, current.Version);
                        lastSnapshot = lastHeartbeat = now;
                    }
                    else if (TimeProvider.System.GetElapsedTime(lastHeartbeat, now) >= HeartbeatInterval)
                    {
                        await writer.WriteAsync(": heartbeat\n\n".AsMemory(), ct);
                        await writer.FlushAsync(ct);
                        lastHeartbeat = now;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client disconnected.
            }
        }, "text/event-stream; charset=utf-8");
    }

    private static async Task WriteSnapshotAsync(StreamWriter writer, Snapshot snapshot, CancellationToken ct)
    {
        var id = snapshot.EventId is null ? "" : $"id: {snapshot.EventId}\n";
        var data = JsonSerializer.Serialize(snapshot.Payload, Json);
        await writer.WriteAsync($"event: snapshot\n{id}data: {data}\n\n".AsMemory(), ct);
        await writer.FlushAsync(ct);
    }
}
