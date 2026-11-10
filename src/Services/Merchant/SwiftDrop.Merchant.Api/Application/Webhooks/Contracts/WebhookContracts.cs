using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;

public record CreateWebhookRequest(string? Url, string? EndpointUrl, IReadOnlyList<string> Events)
{
    public string DestinationUrl => !string.IsNullOrWhiteSpace(Url) ? Url : (EndpointUrl ?? string.Empty);
}

public record OneTimeWebhookResponse(
    Guid Id,
    string EndpointUrl,
    IReadOnlyList<string> Events,
    string Secret,
    int SecretVersion,
    DateTimeOffset CreatedAt);

public record WebhookSubscriptionResponse(
    Guid Id,
    string EndpointUrl,
    IReadOnlyList<string> Events,
    bool IsActive,
    int SecretVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record WebhookPageResponse(
    IReadOnlyList<WebhookSubscriptionResponse> Items,
    int TotalCount,
    int Page,
    int PageSize);

public record WebhookAttemptResponse(
    Guid Id,
    Guid JobId,
    int AttemptNumber,
    DateTimeOffset RequestedAt,
    int? ResponseStatusCode,
    long DurationMs,
    DateTimeOffset? NextRetryAt,
    string? FailureReason);

public record WebhookAttemptPageResponse(
    IReadOnlyList<WebhookAttemptResponse> Items,
    int TotalCount,
    int Page,
    int PageSize);

public record ReplayWebhookRequest(Guid? EventId, Guid? JobId, string Reason);

public record WebhookJobResponse(
    Guid Id,
    Guid EventId,
    Guid SubscriptionId,
    int ReplayGeneration,
    string Status,
    int AttemptCount,
    DateTimeOffset DueAt,
    string? SafeErrorCode,
    DateTimeOffset CreatedAt);

public record InternalDispatchPayload(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    DateTimeOffset OccurredAt,
    Guid ParcelId,
    int ParcelVersion,
    Guid MerchantId,
    string Status);

public record InternalDispatchResponse(
    Guid EventId,
    int SubscriptionsMatched,
    int JobsCreated,
    string Status);

public record WebhookSendResult(
    bool Success,
    int? StatusCode,
    long DurationMs,
    string? FailureReason,
    bool CanRetry,
    TimeSpan? RetryAfter = null);

public record ExternalWebhookPayload(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    DateTimeOffset OccurredAt,
    Guid ParcelId,
    int ParcelVersion,
    string? TrackingNumber,
    string Status);

public static class WebhookJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static byte[] SerializeExternalEvent(
        Guid eventId,
        string eventType,
        int schemaVersion,
        DateTimeOffset occurredAt,
        Guid parcelId,
        int parcelVersion,
        string? trackingNumber,
        string status)
    {
        var dto = new ExternalWebhookPayload(
            eventId,
            eventType,
            schemaVersion,
            occurredAt,
            parcelId,
            parcelVersion,
            trackingNumber,
            status);
        return JsonSerializer.SerializeToUtf8Bytes(dto, Options);
    }
}

public sealed record WebhookFailure(string Code, string Message)
{
    public static WebhookFailure Unauthenticated(string message) => new("unauthenticated", message);
    public static WebhookFailure Forbidden(string message) => new("forbidden", message);
    public static WebhookFailure NotFound(string message) => new("not_found", message);
    public static WebhookFailure Conflict(string message) => new("conflict", message);
    public static WebhookFailure InvalidInput(string message) => new("invalid_input", message);
    public static WebhookFailure ServiceUnavailable(string message) => new("service_unavailable", message);
}
