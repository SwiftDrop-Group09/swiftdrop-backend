namespace SwiftDrop.Delivery.Api.Domain;

public static class ProofCompletionPolicy
{
    public static void Validate(IReadOnlyList<ProofMetadata> proofs, Guid parcelId)
    {
        if (proofs.Count is < 1 or > 2 || proofs.Any(x => x.ParcelId != parcelId)
            || proofs.Select(x => x.Type).Distinct().Count() != proofs.Count
            || proofs.Select(x => x.StorageRef).Distinct(StringComparer.Ordinal).Count() != proofs.Count)
            throw new ArgumentException("At least one distinct, parcel-bound photo or signature is required.", nameof(proofs));
    }
}
