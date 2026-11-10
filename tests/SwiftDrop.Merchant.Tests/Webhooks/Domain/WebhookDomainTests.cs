using SwiftDrop.Merchant.Api.Domain.Webhooks;

namespace SwiftDrop.Merchant.Tests.Webhooks.Domain;

public sealed class WebhookDomainTests
{
    [Fact]
    public void WebhookSubscription_Create_SetsExpectedInitialState()
    {
        var businessId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var sub = WebhookSubscription.Create(
            businessId,
            "https://merchant.example.com/webhook",
            [WebhookEvents.DeliveryDelivered, WebhookEvents.DeliveryCreated],
            secretCiphertext: [1, 2, 3],
            secretNonce: new byte[12],
            secretTag: new byte[16],
            keyVersion: 1,
            now);

        Assert.NotEqual(Guid.Empty, sub.Id);
        Assert.Equal(businessId, sub.BusinessId);
        Assert.Equal("https://merchant.example.com/webhook", sub.EndpointUrl);
        Assert.True(sub.IsActive);
        Assert.Equal(1, sub.SecretVersion);
        Assert.Equal(1, sub.KeyVersion);
        Assert.True(sub.IsSubscribedTo(WebhookEvents.DeliveryDelivered));
        Assert.True(sub.IsSubscribedTo(WebhookEvents.DeliveryCreated));
        Assert.False(sub.IsSubscribedTo(WebhookEvents.DeliveryFailed));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com:8443/webhook")]
    [InlineData("not-a-url")]
    public void WebhookGuard_HttpsUrl_RejectsInvalidUrl(string invalidUrl)
    {
        Assert.Throws<ArgumentException>(() => WebhookGuard.HttpsUrl(invalidUrl, "endpointUrl"));
    }

    [Fact]
    public void WebhookSubscription_Deactivate_MarksInactive()
    {
        var sub = WebhookSubscription.Create(
            Guid.NewGuid(),
            "https://merchant.example.com/webhook",
            [WebhookEvents.DeliveryDelivered],
            [1],
            new byte[12],
            new byte[16],
            1);

        Assert.True(sub.IsActive);
        sub.Deactivate(DateTimeOffset.UtcNow);
        Assert.False(sub.IsActive);
        Assert.False(sub.IsSubscribedTo(WebhookEvents.DeliveryDelivered));
    }

    [Fact]
    public void WebhookDeliveryJob_StatusTransitions_FollowLifecycle()
    {
        var now = DateTimeOffset.UtcNow;
        var job = WebhookDeliveryJob.CreateAutomatic(
            eventId: Guid.NewGuid(),
            subscriptionId: Guid.NewGuid(),
            frozenEventBytes: [10, 20],
            secretVersion: 1,
            requiresResolution: false,
            now: now);

        Assert.Equal(WebhookStatus.Pending, job.Status);
        Assert.Equal(0, job.AttemptCount);

        // Claim
        var leaseToken = Guid.NewGuid();
        job.Claim(now.AddMinutes(5), leaseToken, now);
        Assert.Equal(WebhookStatus.Processing, job.Status);
        Assert.Equal(leaseToken, job.LeaseToken);

        // Record successful attempt
        job.RecordAttempt(delivered: true, nextRetryAt: null, safeErrorCode: null, now.AddSeconds(1));
        Assert.Equal(WebhookStatus.Delivered, job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.Null(job.LeasedUntil);
    }

    [Fact]
    public void WebhookDeliveryJob_CreateReplay_PreservesOriginalEventId()
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var subId = Guid.NewGuid();
        var businessId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        var job = WebhookDeliveryJob.CreateAutomatic(
            eventId,
            subId,
            [10, 20],
            secretVersion: 1,
            now: now);

        // Record failed attempt after max attempts
        job.RecordAttempt(delivered: false, nextRetryAt: null, safeErrorCode: "http_500", now);
        // Force to Failed
        while (job.AttemptCount < 6)
        {
            job.RecordAttempt(delivered: false, nextRetryAt: null, safeErrorCode: "http_500", now);
        }
        Assert.Equal(WebhookStatus.Failed, job.Status);

        var (replayJob, audit) = job.CreateReplay(actorId, businessId, "Outage resolved", now);

        Assert.Equal(eventId, replayJob.EventId);
        Assert.Equal(1, replayJob.ReplayGeneration);
        Assert.Equal(WebhookStatus.Pending, replayJob.Status);
        Assert.Equal(0, replayJob.AttemptCount);

        Assert.Equal(job.Id, audit.OriginalJobId);
        Assert.Equal(replayJob.Id, audit.ReplayJobId);
        Assert.Equal("Outage resolved", audit.Reason);
    }

    [Theory]
    [InlineData("Created", WebhookEvents.DeliveryCreated)]
    [InlineData("Assigned", WebhookEvents.DeliveryAssigned)]
    [InlineData("PickedUp", WebhookEvents.DeliveryPickedUp)]
    [InlineData("InTransit", WebhookEvents.DeliveryInTransit)]
    [InlineData("OutForDelivery", WebhookEvents.DeliveryOutForDelivery)]
    [InlineData("Delivered", WebhookEvents.DeliveryDelivered)]
    [InlineData("Failed", WebhookEvents.DeliveryFailed)]
    [InlineData("Cancelled", WebhookEvents.DeliveryCancelled)]
    public void WebhookEventMapping_MapsCanonicalStatusesCorrectly(string status, string expectedEvent)
    {
        var mapped = WebhookEventMapping.TryMapStatusToEvent(status);
        Assert.Equal(expectedEvent, mapped);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 5)]
    [InlineData(4, 15)]
    [InlineData(5, 30)]
    [InlineData(6, 60)]
    public void WebhookRetryPolicy_DelaysMatchDocumentedSchedule(int attempt, int expectedMinutes)
    {
        var delay = WebhookRetryPolicy.GetScheduledDelay(attempt);
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), delay);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(200, false)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    public void WebhookRetryPolicy_IsRetryableStatusCode_ClassifiesCorrectly(int? statusCode, bool expectedRetryable)
    {
        Assert.Equal(expectedRetryable, WebhookRetryPolicy.IsRetryableStatusCode(statusCode));
    }
}
