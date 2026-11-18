using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftDrop.Contracts.Events.V1;
using SwiftDrop.Notification.Worker.Messaging;
using SwiftDrop.Notification.Worker.Persistence;
using SwiftDrop.Notification.Worker.Routing;

namespace SwiftDrop.Notification.Tests;

public sealed class StatusEffectRoutingWorkerTests
{
    [Fact]
    public async Task MerchantStatusEventIsPostedWithServiceTokenAndDurablyAccepted()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-17T10:00:00Z"));
        var message = MerchantEvent();
        await CreateDispatchJobAsync(database, clock, message);
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        var worker = CreateWorker(database, clock, handler, "worker.jwt");

        Assert.True(await worker.ProcessPendingAsync(CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/internal/v1/webhook-dispatches", request.Uri!.AbsolutePath);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal("test-short-lived-worker-token", request.AuthorizationParameter);
        using var payload = JsonDocument.Parse(request.Body!);
        var root = payload.RootElement;
        Assert.Equal(message.EventId, root.GetProperty("eventId").GetGuid());
        Assert.Equal("StatusChanged", root.GetProperty("eventType").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Guid.Parse(message.ParcelId), root.GetProperty("parcelId").GetGuid());
        Assert.Equal(message.ParcelVersion, root.GetProperty("parcelVersion").GetInt32());
        Assert.Equal(Guid.Parse(message.MerchantId!), root.GetProperty("merchantId").GetGuid());
        Assert.Equal(message.Status, root.GetProperty("status").GetString());
        Assert.Equal(message.OccurredAt, root.GetProperty("occurredAt").GetDateTimeOffset());

        var job = await database.ReadDispatchJobAsync();
        Assert.Equal("Accepted", job.State);
        Assert.Equal(1, job.DispatchAttempts);
        Assert.Null(job.LastErrorCode);
        Assert.Null(job.NextAttemptAt);
        Assert.Null(job.LeaseUntil);
    }

    [Fact]
    public async Task MerchantFailureRemainsDurableAndRetrySucceedsAfterBackoff()
    {
        await using var database = await TestDatabase.CreateAsync();
        var initial = DateTimeOffset.Parse("2026-11-17T10:00:00Z");
        var clock = new ManualTimeProvider(initial);
        await CreateDispatchJobAsync(database, clock, MerchantEvent());
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.Accepted);
        var worker = CreateWorker(database, clock, handler, "worker.jwt");

        Assert.True(await worker.ProcessPendingAsync(CancellationToken.None));
        var deferred = await database.ReadDispatchJobAsync();
        Assert.Equal("Pending", deferred.State);
        Assert.Equal(1, deferred.DispatchAttempts);
        Assert.Equal("merchant_http_503", deferred.LastErrorCode);
        Assert.Equal(initial + TimeSpan.FromSeconds(5), deferred.NextAttemptAt);
        Assert.Null(deferred.LeaseUntil);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await worker.ProcessPendingAsync(CancellationToken.None));

        var accepted = await database.ReadDispatchJobAsync();
        Assert.Equal("Accepted", accepted.State);
        Assert.Equal(2, accepted.DispatchAttempts);
        Assert.Null(accepted.LastErrorCode);
        Assert.Null(accepted.NextAttemptAt);
        Assert.Null(accepted.LeaseUntil);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task MissingMerchantTokenLeavesDispatchPendingUntilTokenFileAppears()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-17T10:00:00Z"));
        await CreateDispatchJobAsync(database, clock, MerchantEvent());
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        var worker = CreateWorker(database, clock, handler, "worker.jwt", tokenValue: null);

        Assert.False(await worker.ProcessPendingAsync(CancellationToken.None));
        var pending = await database.ReadDispatchJobAsync();
        Assert.Equal("Pending", pending.State);
        Assert.Equal(0, pending.DispatchAttempts);
        Assert.Null(pending.NextAttemptAt);
        Assert.Null(pending.LeaseUntil);
        Assert.Empty(handler.Requests);

        File.WriteAllText(Path.Combine(database.Directory, "worker.jwt"), "rotated-short-lived-worker-token");
        Assert.True(await worker.ProcessPendingAsync(CancellationToken.None));

        var accepted = await database.ReadDispatchJobAsync();
        Assert.Equal("Accepted", accepted.State);
        Assert.Equal(1, accepted.DispatchAttempts);
        Assert.Equal("rotated-short-lived-worker-token", Assert.Single(handler.Requests).AuthorizationParameter);
    }

    private static async Task CreateDispatchJobAsync(
        TestDatabase database,
        TimeProvider clock,
        StatusChangedV1 message)
    {
        await using var scope = database.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        var processor = new StatusNotificationProcessor(
            context, clock, NullLogger<StatusNotificationProcessor>.Instance);
        await processor.ProcessAsync(message, CancellationToken.None);
    }

    private static StatusEffectRoutingWorker CreateWorker(
        TestDatabase database,
        TimeProvider clock,
        RecordingHandler handler,
        string tokenFileName,
        string? tokenValue = "test-short-lived-worker-token")
    {
        var tokenPath = Path.Combine(database.Directory, tokenFileName);
        if (tokenValue is not null)
        {
            File.WriteAllText(tokenPath, tokenValue);
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Notification:ServiceTokenFiles:Merchant"] = tokenPath
            }).Build();
        return new StatusEffectRoutingWorker(
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            new RecordingHttpClientFactory(handler),
            new ServiceJwtFileReader(configuration),
            new NotificationProviderJobProcessor(
                new NoOpNotificationProvider(), clock, NullLogger<NotificationProviderJobProcessor>.Instance),
            clock,
            NullLogger<StatusEffectRoutingWorker>.Instance);
    }

    private static StatusChangedV1 MerchantEvent() => new(
        Guid.NewGuid(),
        StatusChangedV1.CanonicalEventType,
        StatusChangedV1.CurrentSchemaVersion,
        DateTimeOffset.Parse("2026-11-17T09:59:00Z"),
        Guid.NewGuid().ToString("D"),
        2,
        Guid.NewGuid().ToString("D"),
        "Delivered",
        "OutForDelivery",
        "merchant-status-correlation");

    private sealed class TestDatabase(SqliteConnection connection, ServiceProvider services, string directory) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public string Directory { get; } = directory;

        public async Task<WebhookDispatchJob> ReadDispatchJobAsync()
        {
            await using var scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<NotificationDbContext>()
                .WebhookDispatchJobs.SingleAsync();
        }

        public static async Task<TestDatabase> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "swiftdrop-notification-routing-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection()
                .AddDbContext<NotificationDbContext>(options => options.UseSqlite(connection))
                .BuildServiceProvider();
            await using (var scope = services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<NotificationDbContext>().Database.EnsureCreatedAsync();
            }

            return new TestDatabase(connection, services, directory);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await connection.DisposeAsync();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }

    private sealed class RecordingHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://merchant.test")
        };
    }

    private sealed class RecordingHandler(params HttpStatusCode[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> responses = new(responses);
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(responses.Dequeue());
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri? Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string? Body);

    private sealed class NoOpNotificationProvider : INotificationProvider
    {
        public Task<NotificationProviderResult> SendMockSmsAsync(long notificationJobId, Guid eventId,
            Guid recipientId, string status, CancellationToken cancellationToken) =>
            Task.FromResult(new NotificationProviderResult(true, null));
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset current = current;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current += amount;
    }
}
