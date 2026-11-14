using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;
using SwiftDrop.Merchant.Api.Domain.Webhooks;
using SwiftDrop.Merchant.Tests.Webhooks.TestDoubles;

namespace SwiftDrop.Merchant.Tests.Webhooks;

public sealed class AttemptAndReplayApplicationTests
{
    private readonly InMemoryWebhookSubscriptionStore _subscriptionStore = new();
    private readonly InMemoryWebhookDispatchStore _dispatchStore = new();
    private readonly FakeWebhookSecretProtector _secretProtector = new();
    private readonly FakeWebhookDestinationValidator _destinationValidator = new();
    private readonly FakeWebhookSender _sender = new();
    private readonly FakeWebhookClock _clock = new(DateTimeOffset.Parse("2026-11-10T12:00:00Z"));
    private readonly FakeCurrentMerchantContext _merchantContext = new();

    private ListWebhookAttemptsHandler CreateAttemptsHandler() =>
        new(_merchantContext, _subscriptionStore, _dispatchStore);

    private ReplayWebhookJobHandler CreateReplayHandler() =>
        new(_merchantContext, _subscriptionStore, _dispatchStore, _clock);

    private ProcessDueWebhookJobsHandler CreateProcessHandler() =>
        new(_dispatchStore, _subscriptionStore, _secretProtector, _destinationValidator, _sender, _clock);

    [Fact]
    public async Task ListAttempts_WhenForeignMerchantAttemptsInspection_ReturnsNotFound()
    {
        var otherMerchantId = Guid.NewGuid();
        var subscription = WebhookSubscription.Create(
            otherMerchantId,
            "https://foreign.example.com",
            [WebhookEvents.DeliveryDelivered],
            [1, 2, 3],
            new byte[12],
            new byte[16],
            1,
            _clock.UtcNow);
        await _subscriptionStore.AddAsync(subscription, CancellationToken.None);

        var handler = CreateAttemptsHandler();
        var (response, failure) = await handler.HandleAsync(subscription.Id, jobId: null, page: 1, pageSize: 20, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Equal("not_found", failure.Code);
        Assert.Null(response);
    }

    [Fact]
    public async Task ReplayWebhookJob_WhenJobFailed_CreatesReplayJobAndAuditRecord()
    {
        var businessId = _merchantContext.BusinessId!.Value;
        var subscription = WebhookSubscription.Create(
            businessId,
            "https://merchant.example.com",
            [WebhookEvents.DeliveryDelivered],
            [1, 2, 3],
            new byte[12],
            new byte[16],
            1,
            _clock.UtcNow);
        await _subscriptionStore.AddAsync(subscription, CancellationToken.None);

        var eventId = Guid.NewGuid();
        var originalJob = WebhookDeliveryJob.CreateAutomatic(
            eventId,
            subscription.Id,
            [10, 20, 30],
            secretVersion: 1,
            now: _clock.UtcNow);

        // Fail the job after max attempts
        for (var i = 0; i < 6; i++)
        {
            originalJob.RecordAttempt(delivered: false, nextRetryAt: null, safeErrorCode: "http_500", _clock.UtcNow);
        }
        Assert.Equal(WebhookStatus.Failed, originalJob.Status);
        await _dispatchStore.AddJobsAsync([originalJob], CancellationToken.None);

        var replayHandler = CreateReplayHandler();
        var request = new ReplayWebhookRequest(
            EventId: eventId,
            JobId: null,
            Reason: "Merchant receiver had an outage; ready to re-receive.");

        var (jobResponse, failure) = await replayHandler.HandleAsync(subscription.Id, request, CancellationToken.None);

        Assert.Null(failure);
        Assert.NotNull(jobResponse);
        Assert.Equal(eventId, jobResponse.EventId); // Preserves original eventId
        Assert.Equal(1, jobResponse.ReplayGeneration); // Increments replay generation
        Assert.Equal(WebhookStatus.Pending.ToString(), jobResponse.Status);
        Assert.Equal(0, jobResponse.AttemptCount);

        // Verify audit log
        Assert.Single(_dispatchStore.Audits);
        var audit = _dispatchStore.Audits[0];
        Assert.Equal(originalJob.Id, audit.OriginalJobId);
        Assert.Equal(jobResponse.Id, audit.ReplayJobId);
        Assert.Equal(businessId, audit.BusinessId);
        Assert.Equal("Merchant receiver had an outage; ready to re-receive.", audit.Reason);
    }

    [Fact]
    public async Task ReplayWebhookJob_WhenJobNotFailed_ReturnsConflict()
    {
        var businessId = _merchantContext.BusinessId!.Value;
        var subscription = WebhookSubscription.Create(
            businessId,
            "https://merchant.example.com",
            [WebhookEvents.DeliveryDelivered],
            [1, 2, 3],
            new byte[12],
            new byte[16],
            1,
            _clock.UtcNow);
        await _subscriptionStore.AddAsync(subscription, CancellationToken.None);

        var eventId = Guid.NewGuid();
        var pendingJob = WebhookDeliveryJob.CreateAutomatic(
            eventId,
            subscription.Id,
            [10, 20, 30],
            secretVersion: 1,
            now: _clock.UtcNow);
        await _dispatchStore.AddJobsAsync([pendingJob], CancellationToken.None);

        var replayHandler = CreateReplayHandler();
        var request = new ReplayWebhookRequest(EventId: eventId, JobId: null, Reason: "premature replay");

        var (jobResponse, failure) = await replayHandler.HandleAsync(subscription.Id, request, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Equal("conflict", failure.Code);
        Assert.Null(jobResponse);
    }

    [Fact]
    public async Task ProcessDueJobs_SucceedsOnFirstAttempt()
    {
        var businessId = _merchantContext.BusinessId!.Value;
        var subscription = WebhookSubscription.Create(
            businessId,
            "https://merchant.example.com",
            [WebhookEvents.DeliveryDelivered],
            [1, 2, 3],
            new byte[12],
            new byte[16],
            1,
            _clock.UtcNow);
        await _subscriptionStore.AddAsync(subscription, CancellationToken.None);

        var job = WebhookDeliveryJob.CreateAutomatic(
            Guid.NewGuid(),
            subscription.Id,
            [10, 20, 30],
            secretVersion: 1,
            now: _clock.UtcNow);
        await _dispatchStore.AddJobsAsync([job], CancellationToken.None);

        _sender.SendHandler = (_, _, _, _, _) => new WebhookSendResult(true, 200, 35, null, false);

        var processHandler = CreateProcessHandler();
        var processed = await processHandler.ProcessBatchAsync(10, TimeSpan.FromMinutes(5), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(1, processed);
        var updatedJob = await _dispatchStore.GetJobByIdAsync(job.Id, CancellationToken.None);
        Assert.NotNull(updatedJob);
        Assert.Equal(WebhookStatus.Delivered, updatedJob.Status);
        Assert.Equal(1, updatedJob.AttemptCount);

        Assert.Single(_dispatchStore.Attempts);
        var attempt = _dispatchStore.Attempts[0];
        Assert.Equal(200, attempt.ResponseStatusCode);
        Assert.Null(attempt.NextRetryAt);
    }

    [Fact]
    public async Task ProcessDueJobs_AppliesRetryScheduleOnTransientFailure()
    {
        var businessId = _merchantContext.BusinessId!.Value;
        var subscription = WebhookSubscription.Create(
            businessId,
            "https://merchant.example.com",
            [WebhookEvents.DeliveryDelivered],
            [1, 2, 3],
            new byte[12],
            new byte[16],
            1,
            _clock.UtcNow);
        await _subscriptionStore.AddAsync(subscription, CancellationToken.None);

        var job = WebhookDeliveryJob.CreateAutomatic(
            Guid.NewGuid(),
            subscription.Id,
            [10, 20, 30],
            secretVersion: 1,
            now: _clock.UtcNow);
        await _dispatchStore.AddJobsAsync([job], CancellationToken.None);

        // Attempt 1 fails with 503
        _sender.SendHandler = (_, _, _, _, _) => new WebhookSendResult(false, 503, 120, "Service Unavailable", CanRetry: true);

        var processHandler = CreateProcessHandler();
        await processHandler.ProcessBatchAsync(10, TimeSpan.FromMinutes(5), Guid.NewGuid(), CancellationToken.None);

        var updatedJob = await _dispatchStore.GetJobByIdAsync(job.Id, CancellationToken.None);
        Assert.NotNull(updatedJob);
        Assert.Equal(WebhookStatus.Pending, updatedJob.Status);
        Assert.Equal(1, updatedJob.AttemptCount);
        // Next retry for attempt 2 is 1 minute
        Assert.Equal(_clock.UtcNow.AddMinutes(1), updatedJob.DueAt);

        Assert.Single(_dispatchStore.Attempts);
        Assert.Equal(503, _dispatchStore.Attempts[0].ResponseStatusCode);
        Assert.Equal(_clock.UtcNow.AddMinutes(1), _dispatchStore.Attempts[0].NextRetryAt);
    }

    [Fact]
    public async Task ProcessDueJobs_HonorsRetryAfterHeader()
    {
        var businessId = _merchantContext.BusinessId!.Value;
        var subscription = WebhookSubscription.Create(
            businessId,
            "https://merchant.example.com",
            [WebhookEvents.DeliveryDelivered],
            [1, 2, 3],
            new byte[12],
            new byte[16],
            1,
            _clock.UtcNow);
        await _subscriptionStore.AddAsync(subscription, CancellationToken.None);

        var job = WebhookDeliveryJob.CreateAutomatic(
            Guid.NewGuid(),
            subscription.Id,
            [10, 20, 30],
            secretVersion: 1,
            now: _clock.UtcNow);
        await _dispatchStore.AddJobsAsync([job], CancellationToken.None);

        // Attempt 1 fails with 429 and Retry-After = 10 minutes (which is > default 1 minute)
        _sender.SendHandler = (_, _, _, _, _) => new WebhookSendResult(
            false,
            429,
            40,
            "Too Many Requests",
            CanRetry: true,
            RetryAfter: TimeSpan.FromMinutes(10));

        var processHandler = CreateProcessHandler();
        await processHandler.ProcessBatchAsync(10, TimeSpan.FromMinutes(5), Guid.NewGuid(), CancellationToken.None);

        var updatedJob = await _dispatchStore.GetJobByIdAsync(job.Id, CancellationToken.None);
        Assert.NotNull(updatedJob);
        Assert.Equal(WebhookStatus.Pending, updatedJob.Status);
        Assert.Equal(1, updatedJob.AttemptCount);
        Assert.Equal(_clock.UtcNow.AddMinutes(10), updatedJob.DueAt);
    }

    [Fact]
    public async Task ProcessDueJobs_TransitionsToFailedAfterSixAttempts()
    {
        var businessId = _merchantContext.BusinessId!.Value;
        var subscription = WebhookSubscription.Create(
            businessId,
            "https://merchant.example.com",
            [WebhookEvents.DeliveryDelivered],
            [1, 2, 3],
            new byte[12],
            new byte[16],
            1,
            _clock.UtcNow);
        await _subscriptionStore.AddAsync(subscription, CancellationToken.None);

        var job = WebhookDeliveryJob.CreateAutomatic(
            Guid.NewGuid(),
            subscription.Id,
            [10, 20, 30],
            secretVersion: 1,
            now: _clock.UtcNow);
        await _dispatchStore.AddJobsAsync([job], CancellationToken.None);

        _sender.SendHandler = (_, _, _, _, _) => new WebhookSendResult(false, 500, 50, "Internal Server Error", CanRetry: true);
        var processHandler = CreateProcessHandler();

        // Run through all 6 attempts
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            await processHandler.ProcessBatchAsync(10, TimeSpan.FromMinutes(5), Guid.NewGuid(), CancellationToken.None);

            var current = await _dispatchStore.GetJobByIdAsync(job.Id, CancellationToken.None);
            Assert.NotNull(current);
            Assert.Equal(attempt, current.AttemptCount);

            if (attempt < 6)
            {
                Assert.Equal(WebhookStatus.Pending, current.Status);
                Assert.True(current.DueAt > _clock.UtcNow);
                // Advance clock past dueAt
                _clock.UtcNow = current.DueAt.AddSeconds(1);
            }
            else
            {
                // Terminal after 6th attempt
                Assert.Equal(WebhookStatus.Failed, current.Status);
            }
        }
    }

    [Fact]
    public async Task TriggerTestWebhook_WhenApprovedMerchant_EnqueuesAutomaticTestJob()
    {
        var businessId = _merchantContext.BusinessId!.Value;
        var subscription = WebhookSubscription.Create(
            businessId,
            "https://merchant.example.com/webhooks",
            [WebhookEvents.DeliveryDelivered],
            [1, 2, 3],
            new byte[12],
            new byte[16],
            1,
            _clock.UtcNow);
        await _subscriptionStore.AddAsync(subscription, CancellationToken.None);

        var handler = new TriggerTestWebhookHandler(_merchantContext, _subscriptionStore, _dispatchStore, _clock);
        var (response, failure) = await handler.HandleAsync(subscription.Id, CancellationToken.None);

        Assert.Null(failure);
        Assert.NotNull(response);
        Assert.Equal(subscription.Id, response.SubscriptionId);
        Assert.Equal("Pending", response.Status);
        Assert.Equal(0, response.AttemptCount);

        var queuedJob = await _dispatchStore.GetJobByIdAsync(response.Id, CancellationToken.None);
        Assert.NotNull(queuedJob);
        Assert.Equal(subscription.Id, queuedJob.SubscriptionId);
        Assert.Equal(WebhookStatus.Pending, queuedJob.Status);
    }

    [Fact]
    public async Task TriggerTestWebhook_WhenForeignSubscription_ReturnsNotFound()
    {
        var foreignBusinessId = Guid.NewGuid();
        var subscription = WebhookSubscription.Create(
            foreignBusinessId,
            "https://other.example.com/webhooks",
            [WebhookEvents.DeliveryDelivered],
            [1, 2, 3],
            new byte[12],
            new byte[16],
            1,
            _clock.UtcNow);
        await _subscriptionStore.AddAsync(subscription, CancellationToken.None);

        var handler = new TriggerTestWebhookHandler(_merchantContext, _subscriptionStore, _dispatchStore, _clock);
        var (response, failure) = await handler.HandleAsync(subscription.Id, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Equal("not_found", failure.Code);
        Assert.Null(response);
    }
}
