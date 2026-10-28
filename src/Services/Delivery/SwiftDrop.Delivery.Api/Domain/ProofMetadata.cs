namespace SwiftDrop.Delivery.Api.Domain;

/// <summary>Private metadata for an object whose persistence the storage adapter has confirmed.</summary>
/// <remarks>Constructing metadata does not authorize an upload or complete a delivery.</remarks>
public sealed class ProofMetadata
{
    private ProofMetadata() { }

    public ProofMetadata(Guid id, Guid parcelId, ProofType type, string storageRef,
        string recipientName, Guid uploadedBy, DateTimeOffset uploadedAt)
    {
        Id = DomainGuard.Id(id, nameof(id));
        ParcelId = DomainGuard.Id(parcelId, nameof(parcelId));
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        Type = type;
        StorageRef = DomainGuard.Text(storageRef, nameof(storageRef));
        // Persist a private object reference, never a public or presigned URL.
        if (Uri.TryCreate(storageRef, UriKind.Absolute, out _) || storageRef.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("An opaque private object reference is required.", nameof(storageRef));
        }

        RecipientName = DomainGuard.Text(recipientName, nameof(recipientName));
        if (recipientName.Length > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(recipientName));
        }

        UploadedBy = DomainGuard.Id(uploadedBy, nameof(uploadedBy));
        UploadedAt = uploadedAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public Guid ParcelId { get; private set; }
    public ProofType Type { get; private set; }
    public string StorageRef { get; private set; } = null!;
    public string RecipientName { get; private set; } = null!;
    public DateTimeOffset UploadedAt { get; private set; }
    public Guid UploadedBy { get; private set; }
}
