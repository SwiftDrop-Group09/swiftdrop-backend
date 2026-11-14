using System.Security.Cryptography;
using System.Text;
using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Domain.Webhooks;

namespace SwiftDrop.Merchant.Tests.Webhooks.TestDoubles;

public sealed class InMemoryWebhookSubscriptionStore : IWebhookSubscriptionStore
{
    private readonly List<WebhookSubscription> _subscriptions = [];

    public Task<WebhookSubscription?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var item = _subscriptions.FirstOrDefault(s => s.Id == id);
        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<WebhookSubscription>> ListByBusinessIdAsync(Guid businessId, CancellationToken ct)
    {
        var items = _subscriptions.Where(s => s.BusinessId == businessId).ToList();
        return Task.FromResult<IReadOnlyList<WebhookSubscription>>(items);
    }

    public Task<IReadOnlyList<WebhookSubscription>> ListActiveByBusinessAndEventAsync(Guid businessId, string eventName, CancellationToken ct)
    {
        var items = _subscriptions
            .Where(s => s.BusinessId == businessId && s.IsActive && s.IsSubscribedTo(eventName))
            .ToList();
        return Task.FromResult<IReadOnlyList<WebhookSubscription>>(items);
    }

    public Task AddAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        _subscriptions.Add(subscription);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        var index = _subscriptions.FindIndex(s => s.Id == subscription.Id);
        if (index >= 0)
        {
            _subscriptions[index] = subscription;
        }
        return Task.CompletedTask;
    }
}

public sealed class InMemoryWebhookDispatchStore : IWebhookDispatchStore
{
    private readonly List<WebhookDeliveryJob> _jobs = [];
    private readonly List<WebhookAttempt> _attempts = [];
    private readonly List<WebhookReplayAudit> _audits = [];

    public IReadOnlyList<WebhookDeliveryJob> Jobs => _jobs;
    public IReadOnlyList<WebhookAttempt> Attempts => _attempts;
    public IReadOnlyList<WebhookReplayAudit> Audits => _audits;

    public Task AddJobsAsync(IEnumerable<WebhookDeliveryJob> jobs, CancellationToken ct)
    {
        _jobs.AddRange(jobs);
        return Task.CompletedTask;
    }

    public Task<WebhookDeliveryJob?> GetJobByIdAsync(Guid jobId, CancellationToken ct)
    {
        return Task.FromResult(_jobs.FirstOrDefault(j => j.Id == jobId));
    }

    public Task<WebhookDeliveryJob?> GetJobBySubscriptionAndEventAsync(Guid subscriptionId, Guid eventId, int replayGeneration, CancellationToken ct)
    {
        return Task.FromResult(_jobs.FirstOrDefault(j =>
            j.SubscriptionId == subscriptionId &&
            j.EventId == eventId &&
            j.ReplayGeneration == replayGeneration));
    }

    public Task<WebhookDeliveryJob?> GetLatestJobByEventAsync(Guid subscriptionId, Guid eventId, CancellationToken ct)
    {
        return Task.FromResult(_jobs
            .Where(j => j.SubscriptionId == subscriptionId && j.EventId == eventId)
            .OrderByDescending(j => j.ReplayGeneration)
            .FirstOrDefault());
    }

    public Task UpdateJobAsync(WebhookDeliveryJob job, CancellationToken ct)
    {
        var index = _jobs.FindIndex(j => j.Id == job.Id);
        if (index >= 0)
        {
            _jobs[index] = job;
        }
        return Task.CompletedTask;
    }

    public Task CancelUnsentJobsBySubscriptionAsync(Guid subscriptionId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var job in _jobs.Where(j => j.SubscriptionId == subscriptionId && (j.Status == WebhookStatus.Pending || j.Status == WebhookStatus.PendingResolution)))
        {
            job.Cancel(now);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WebhookDeliveryJob>> ClaimDueJobsAsync(int batchSize, TimeSpan leaseDuration, Guid leaseToken, DateTimeOffset now, CancellationToken ct)
    {
        var dueJobs = _jobs
            .Where(j => j.Status == WebhookStatus.Pending && j.DueAt <= now && (j.LeasedUntil == null || j.LeasedUntil < now))
            .Take(batchSize)
            .ToList();

        foreach (var job in dueJobs)
        {
            job.Claim(now.Add(leaseDuration), leaseToken, now);
        }

        return Task.FromResult<IReadOnlyList<WebhookDeliveryJob>>(dueJobs);
    }

    public Task RecordAttemptAsync(WebhookAttempt attempt, CancellationToken ct)
    {
        _attempts.Add(attempt);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WebhookAttempt>> ListAttemptsByJobAsync(Guid jobId, int skip, int take, CancellationToken ct)
    {
        var items = _attempts
            .Where(a => a.JobId == jobId)
            .OrderByDescending(a => a.AttemptNumber)
            .Skip(skip)
            .Take(take)
            .ToList();

        return Task.FromResult<IReadOnlyList<WebhookAttempt>>(items);
    }

    public Task<IReadOnlyList<WebhookAttempt>> ListAttemptsBySubscriptionAsync(Guid subscriptionId, Guid? jobId, int skip, int take, CancellationToken ct)
    {
        var jobIds = _jobs.Where(j => j.SubscriptionId == subscriptionId).Select(j => j.Id).ToHashSet();
        var query = _attempts.Where(a => jobIds.Contains(a.JobId));
        if (jobId.HasValue)
        {
            query = query.Where(a => a.JobId == jobId.Value);
        }

        var items = query
            .OrderByDescending(a => a.RequestedAt)
            .Skip(skip)
            .Take(take)
            .ToList();

        return Task.FromResult<IReadOnlyList<WebhookAttempt>>(items);
    }

    public Task<int> CountAttemptsBySubscriptionAsync(Guid subscriptionId, Guid? jobId, CancellationToken ct)
    {
        var jobIds = _jobs.Where(j => j.SubscriptionId == subscriptionId).Select(j => j.Id).ToHashSet();
        var query = _attempts.Where(a => jobIds.Contains(a.JobId));
        if (jobId.HasValue)
        {
            query = query.Where(a => a.JobId == jobId.Value);
        }

        return Task.FromResult(query.Count());
    }

    public Task RecordReplayAuditAsync(WebhookReplayAudit audit, CancellationToken ct)
    {
        _audits.Add(audit);
        return Task.CompletedTask;
    }
}

public sealed class FakeWebhookSecretProtector : IWebhookSecretProtector
{
    public (byte[] Ciphertext, byte[] Nonce, byte[] Tag, int KeyVersion) Encrypt(byte[] plaintext)
    {
        return (plaintext, new byte[12], new byte[16], 1);
    }

    public byte[] Decrypt(byte[] ciphertext, byte[] nonce, byte[] tag, int keyVersion)
    {
        return ciphertext;
    }

    public (string PlaintextSecret, byte[] Ciphertext, byte[] Nonce, byte[] Tag, int KeyVersion) GenerateAndEncrypt()
    {
        var secretBytes = new byte[32];
        RandomNumberGenerator.Fill(secretBytes);
        var hex = Convert.ToHexString(secretBytes).ToLowerInvariant();
        return (hex, secretBytes, new byte[12], new byte[16], 1);
    }
}

public sealed class FakeWebhookDestinationValidator : IWebhookDestinationValidator
{
    public bool AlwaysSafe { get; set; } = true;
    public HashSet<string> BlockedUrls { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> ValidateAsync(string endpointUrl, CancellationToken ct)
    {
        if (BlockedUrls.Contains(endpointUrl))
        {
            return Task.FromResult(false);
        }
        return Task.FromResult(AlwaysSafe);
    }
}

public sealed class FakeWebhookClock(DateTimeOffset initialTime) : IWebhookClock
{
    public DateTimeOffset UtcNow { get; set; } = initialTime;

    public void Advance(TimeSpan span)
    {
        UtcNow = UtcNow.Add(span);
    }
}

public sealed class FakeWebhookSender : IWebhookSender
{
    public Func<string, byte[], byte[], string, int, WebhookSendResult>? SendHandler { get; set; }
    public List<(string Url, byte[] Body, byte[] Secret, string EventId, int Version)> Invocations { get; } = [];

    public Task<WebhookSendResult> SendAsync(
        string endpointUrl,
        byte[] bodyBytes,
        byte[] rawSecret,
        string eventId,
        int secretVersion,
        CancellationToken ct)
    {
        Invocations.Add((endpointUrl, bodyBytes, rawSecret, eventId, secretVersion));
        if (SendHandler != null)
        {
            return Task.FromResult(SendHandler(endpointUrl, bodyBytes, rawSecret, eventId, secretVersion));
        }

        return Task.FromResult(new WebhookSendResult(true, 200, 45, null, false));
    }
}

public sealed class FakeWebhookOrderResolver : IWebhookOrderResolver
{
    public Dictionary<Guid, string> ParcelTrackingMap { get; } = [];

    public Task<string?> TryResolveTrackingNumberAsync(Guid parcelId, Guid businessId, CancellationToken ct)
    {
        return Task.FromResult(ParcelTrackingMap.TryGetValue(parcelId, out var trk) ? trk : null);
    }
}

public sealed class FakeCurrentMerchantContext : ICurrentMerchantContext
{
    public bool IsAuthenticated { get; set; } = true;
    public bool IsApprovedMerchant { get; set; } = true;
    public bool CanManageWebhooks { get; set; } = true;
    public Guid? BusinessId { get; set; } = Guid.NewGuid();
    public Guid? ActorId { get; set; } = Guid.NewGuid();
}
