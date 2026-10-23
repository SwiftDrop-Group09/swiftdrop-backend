using System.Text.Json;

namespace SwiftDrop.Delivery.Api.Domain;

public sealed class IdempotencyRecord
{
    private IdempotencyRecord() { }

    public IdempotencyRecord(Guid id, string principalId, string operation, string key,
        string requestFingerprint, DateTimeOffset createdAt)
    {
        Id = DomainGuard.Id(id, nameof(id));
        PrincipalId = DomainGuard.Text(principalId, nameof(principalId));
        Operation = DomainGuard.Text(operation, nameof(operation));
        Key = DomainGuard.Text(key, nameof(key));
        if (Key.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        RequestFingerprint = DomainGuard.Text(requestFingerprint, nameof(requestFingerprint));
        CreatedAt = createdAt.ToUniversalTime();
        ExpiresAt = CreatedAt.AddDays(30);
    }

    public Guid Id { get; private set; }
    public string PrincipalId { get; private set; } = null!;
    public string Operation { get; private set; } = null!;
    public string Key { get; private set; } = null!;
    public string RequestFingerprint { get; private set; } = null!;
    // JSONB envelope stores body as a string to preserve its exact serialized bytes.
    public string? StoredResponse { get; private set; }
    public Guid? ParcelId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public void Complete(Guid parcelId, int statusCode, string body, string location)
    {
        if (StoredResponse is not null)
        {
            throw new InvalidOperationException("The idempotency result is already complete.");
        }

        if (statusCode != 201)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), "Booking stores the original 201 result.");
        }

        DomainGuard.JsonObject(body, nameof(body));
        DomainGuard.Text(location, nameof(location));
        ParcelId = DomainGuard.Id(parcelId, nameof(parcelId));
        StoredResponse = JsonSerializer.Serialize(new { statusCode, body, location });
    }
}
