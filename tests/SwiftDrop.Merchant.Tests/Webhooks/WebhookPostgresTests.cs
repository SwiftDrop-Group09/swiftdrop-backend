using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Domain.Webhooks;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Network;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Persistence;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Security;
using SwiftDrop.Merchant.Tests.Webhooks.TestDoubles;

namespace SwiftDrop.Merchant.Tests.Webhooks;

public sealed class WebhookPostgresTests : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private readonly PostgresFixture _database = new();
    private readonly FakeWebhookClock _clock = new(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeWebhookSecretProtector _protector = new();
    private readonly FakeWebhookOrderResolver _orders = new();
    private InternalDispatchPayload _event = null!;
    private byte[] _secret = [];

    public async Task InitializeAsync()
    {
        await _database.InitializePrivateAsync();
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync("CREATE ROLE merchant_app NOLOGIN");
        await context.Database.MigrateAsync();
        var business = Business.Register(Guid.NewGuid(), "Webhook test merchant", "merchant@example.invalid",
            "+10000000000", "Test address");
        business.Approve(Guid.NewGuid());
        var secret = _protector.GenerateAndEncrypt();
        _secret = secret.Ciphertext;
        var subscription = WebhookSubscription.Create(business.Id, "https://receiver.example.invalid/webhook",
            [WebhookEvents.DeliveryDelivered], secret.Ciphertext, secret.Nonce, secret.Tag, secret.KeyVersion, _clock.UtcNow);
        context.Businesses.Add(business);
        context.WebhookSubscriptions.Add(subscription);
        await context.SaveChangesAsync();
        _event = new(Guid.NewGuid(), "StatusChanged", 1, _clock.UtcNow, Guid.NewGuid(), 8, business.Id, "Delivered");
        _orders.ParcelTrackingMap.Add(_event.ParcelId, "TRACK-TEST");
    }

    [Fact]
    public async Task NonRetryableHttpFailurePersistsOneTerminalAttempt()
    {
        Assert.Equal(1, await AcceptEventAsync());
        using var client = new HttpClient(new RejectingReceiver());
        using var sender = new ResilientWebhookSender(client);

        Assert.Equal(1, await SendAsync(sender));
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, await SendAsync(sender));

        await using var observed = CreateContext();
        var job = await observed.WebhookDispatchJobs.SingleAsync();
        var attempt = await observed.WebhookAttempts.SingleAsync();
        Assert.Equal(WebhookStatus.Failed, job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal("http_400", job.SafeErrorCode);
        Assert.Equal(400, attempt.ResponseStatusCode);
        Assert.Null(attempt.NextRetryAt);
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task ExpiredClaimCanBeRecoveredAndCannotOverwriteTheWinningAttempt()
    {
        Assert.Equal(1, await AcceptEventAsync());
        await using var originalContext = CreateContext();
        var originalStore = new PostgresWebhookDispatchStore(originalContext);
        var oldJob = Assert.Single(await originalStore.ClaimDueJobsAsync(1, Lease, Guid.NewGuid(), _clock.UtcNow, default));
        var oldToken = oldJob.LeaseToken;

        await using var replacementContext = CreateContext();
        var replacementStore = new PostgresWebhookDispatchStore(replacementContext);
        Assert.Empty(await replacementStore.ClaimDueJobsAsync(1, Lease, Guid.NewGuid(), _clock.UtcNow, default));

        _clock.Advance(Lease);
        var newJob = Assert.Single(await replacementStore.ClaimDueJobsAsync(1, Lease, Guid.NewGuid(), _clock.UtcNow, default));
        Assert.Equal(oldJob.Id, newJob.Id);
        Assert.NotEqual(oldToken, newJob.LeaseToken);
        Assert.Equal(0, newJob.AttemptCount);
        await replacementStore.CancelUnsentJobsBySubscriptionAsync(newJob.SubscriptionId, _clock.UtcNow, default);

        oldJob.RecordAttempt(false, _clock.UtcNow.AddMinutes(1), "http_503", _clock.UtcNow);
        var outdated = WebhookAttempt.Record(oldJob.Id, 1, _clock.UtcNow, 503, 10,
            oldJob.DueAt, "Service unavailable", _clock.UtcNow);
        Assert.False(await originalStore.CompleteClaimAsync(oldJob, outdated, default));
        Assert.Empty(await replacementContext.WebhookAttempts.ToListAsync());

        newJob.RecordAttempt(true, null, null, _clock.UtcNow);
        var recorded = WebhookAttempt.Record(newJob.Id, 1, _clock.UtcNow, 204, 10, null, null, _clock.UtcNow);
        Assert.True(await replacementStore.CompleteClaimAsync(newJob, recorded, default));

        await using var observed = CreateContext();
        var job = await observed.WebhookDispatchJobs.SingleAsync();
        var attempt = await observed.WebhookAttempts.SingleAsync();
        Assert.Equal(WebhookStatus.Delivered, job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.Null(job.LeaseToken);
        Assert.Equal(recorded.Id, attempt.Id);
    }

    [Fact]
    public async Task DuplicateAutomaticEventsProduceOneSignedJobAndRetryAfterIsCapped()
    {
        var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AcceptEventAsync()));
        Assert.Equal(1, created.Sum());

        var deliveries = new List<Delivery>();
        var receiverBuilder = WebApplication.CreateBuilder();
        receiverBuilder.WebHost.UseTestServer();
        await using var receiver = receiverBuilder.Build();
        receiver.Run(async context =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            deliveries.Add(new(body.ToArray(), context.Request.Headers["X-SwiftDrop-Event-Id"].ToString(),
                context.Request.Headers["X-SwiftDrop-Timestamp"].ToString(),
                context.Request.Headers["X-SwiftDrop-Signature"].ToString()));
            context.Response.StatusCode = deliveries.Count == 1 ? (int)HttpStatusCode.TooManyRequests : (int)HttpStatusCode.NoContent;
            if (deliveries.Count == 1)
            {
                context.Response.Headers.RetryAfter = "172800";
            }
        });
        await receiver.StartAsync();
        using var client = receiver.GetTestClient();
        using var sender = new ResilientWebhookSender(client);

        Assert.Equal(1, await SendAsync(sender));
        var dueAt = _clock.UtcNow.AddHours(24);
        await using (var failed = CreateContext())
        {
            Assert.Equal(dueAt, (await failed.WebhookDispatchJobs.SingleAsync()).DueAt);
            var attempt = await failed.WebhookAttempts.SingleAsync();
            Assert.Equal(429, attempt.ResponseStatusCode);
            Assert.Equal(dueAt, attempt.NextRetryAt);
        }

        _clock.UtcNow = dueAt.AddSeconds(-1);
        Assert.Equal(0, await SendAsync(sender));
        _clock.UtcNow = dueAt;
        Assert.Equal(1, await SendAsync(sender));

        await using var observed = CreateContext();
        var job = await observed.WebhookDispatchJobs.SingleAsync();
        Assert.Equal(WebhookStatus.Delivered, job.Status);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(new int?[] { 429, 204 }, await observed.WebhookAttempts.OrderBy(attempt => attempt.AttemptNumber)
            .Select(attempt => attempt.ResponseStatusCode).ToArrayAsync());
        Assert.Equal(2, deliveries.Count);
        Assert.Equal(deliveries[0].Body, deliveries[1].Body);
        Assert.All(deliveries, delivery =>
        {
            Assert.Equal(_event.EventId.ToString(), delivery.EventId);
            Assert.True(HmacSignatureProvider.VerifySignature(_secret, delivery.Body, delivery.Timestamp, delivery.Signature));
            using var payload = JsonDocument.Parse(delivery.Body);
            Assert.Equal(_event.EventId, payload.RootElement.GetProperty("eventId").GetGuid());
            Assert.Equal(WebhookEvents.DeliveryDelivered, payload.RootElement.GetProperty("eventType").GetString());
        });
    }

    private MerchantDbContext CreateContext() => new(new DbContextOptionsBuilder<MerchantDbContext>()
        .UseNpgsql(_database.ConnectionString).Options);

    private async Task<int> AcceptEventAsync()
    {
        await using var context = CreateContext();
        var handler = new AcceptStatusChangedDispatchHandler(new PostgresWebhookSubscriptionStore(context),
            new PostgresWebhookDispatchStore(context), _orders, _clock);
        var (response, failure) = await handler.HandleAsync(_event, default);
        Assert.Null(failure);
        return Assert.IsType<InternalDispatchResponse>(response).JobsCreated;
    }

    private async Task<int> SendAsync(ResilientWebhookSender sender)
    {
        await using var context = CreateContext();
        var processor = new ProcessDueWebhookJobsHandler(new PostgresWebhookDispatchStore(context),
            new PostgresWebhookSubscriptionStore(context), _protector, new FakeWebhookDestinationValidator(), sender, _clock);
        return await processor.ProcessBatchAsync(1, Lease, Guid.NewGuid(), default);
    }

    private sealed record Delivery(byte[] Body, string EventId, string Timestamp, string Signature);

    private sealed class RejectingReceiver : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
    }
}
