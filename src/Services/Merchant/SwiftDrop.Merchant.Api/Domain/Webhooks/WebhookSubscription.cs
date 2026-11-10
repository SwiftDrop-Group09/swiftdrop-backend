namespace SwiftDrop.Merchant.Api.Domain.Webhooks;

public sealed class WebhookSubscription
{
    private WebhookSubscription() { }

    public WebhookSubscription(
        Guid id,
        Guid businessId,
        string endpointUrl,
        IReadOnlySet<string> subscribedEvents,
        byte[] secretCiphertext,
        byte[] secretNonce,
        byte[] secretTag,
        int keyVersion,
        int secretVersion,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        bool isActive = true)
    {
        Id = WebhookGuard.Id(id, nameof(id));
        BusinessId = WebhookGuard.Id(businessId, nameof(businessId));
        EndpointUrl = WebhookGuard.HttpsUrl(endpointUrl, nameof(endpointUrl));
        SubscribedEvents = WebhookEvents.ValidateAndNormalize(subscribedEvents);
        SecretCiphertext = WebhookGuard.NonEmptyBytes(secretCiphertext, nameof(secretCiphertext));
        SecretNonce = WebhookGuard.NonEmptyBytes(secretNonce, nameof(secretNonce));
        SecretTag = WebhookGuard.NonEmptyBytes(secretTag, nameof(secretTag));
        KeyVersion = keyVersion > 0 ? keyVersion : throw new ArgumentOutOfRangeException(nameof(keyVersion));
        SecretVersion = secretVersion > 0 ? secretVersion : throw new ArgumentOutOfRangeException(nameof(secretVersion));
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
        IsActive = isActive;
    }

    public WebhookSubscription(
        Guid id,
        Guid businessId,
        string endpointUrl,
        IEnumerable<string> subscribedEvents,
        byte[] secretCiphertext,
        byte[] secretNonce,
        byte[] secretTag,
        int keyVersion,
        int secretVersion,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        bool isActive = true)
        : this(
            id,
            businessId,
            endpointUrl,
            WebhookEvents.ValidateAndNormalize(subscribedEvents),
            secretCiphertext,
            secretNonce,
            secretTag,
            keyVersion,
            secretVersion,
            createdAt,
            updatedAt,
            isActive)
    {
    }

    public static WebhookSubscription Create(
        Guid businessId,
        string endpointUrl,
        IEnumerable<string> subscribedEvents,
        byte[] secretCiphertext,
        byte[] secretNonce,
        byte[] secretTag,
        int keyVersion,
        DateTimeOffset? createdAt = null)
    {
        var timestamp = (createdAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        return new WebhookSubscription(
            Guid.NewGuid(),
            businessId,
            endpointUrl,
            subscribedEvents,
            secretCiphertext,
            secretNonce,
            secretTag,
            keyVersion,
            secretVersion: 1,
            createdAt: timestamp,
            updatedAt: timestamp,
            isActive: true);
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now.ToUniversalTime();
    }

    public bool IsSubscribedTo(string eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName) || !IsActive)
        {
            return false;
        }

        return SubscribedEvents.Contains(eventName.Trim().ToLowerInvariant());
    }

    public Guid Id { get; private set; }
    public Guid BusinessId { get; private set; }
    public string EndpointUrl { get; private set; } = null!;
    public IReadOnlySet<string> SubscribedEvents { get; private set; } = new HashSet<string>();
    public bool IsActive { get; private set; }
    public byte[] SecretCiphertext { get; private set; } = [];
    public byte[] SecretNonce { get; private set; } = [];
    public byte[] SecretTag { get; private set; } = [];
    public int KeyVersion { get; private set; }
    public int SecretVersion { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
