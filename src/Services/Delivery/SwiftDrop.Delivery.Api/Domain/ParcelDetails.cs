namespace SwiftDrop.Delivery.Api.Domain;

// Persistence values supplied by validated application adapters in later phases.
// Address/contact text preserves structured data. Application adapters must select
// ZoneId from deliveryAddress.zoneId for parcel, tariff, and snapshot consistency.
public sealed record ParcelDetails(ParcelSize Size, decimal WeightKg, Guid ZoneId,
    string PickupAddress, string? PickupContact, string DropoffAddress, string DropoffContact)
{
    internal void Validate()
    {
        if (!Enum.IsDefined(Size) || WeightKg <= 0 || WeightKg > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(WeightKg), "Size must be supported and weight must be greater than zero and at most 30 kg.");
        }

        DomainGuard.Id(ZoneId, nameof(ZoneId));
        DomainGuard.Text(PickupAddress, nameof(PickupAddress));
        DomainGuard.Text(DropoffAddress, nameof(DropoffAddress));
        DomainGuard.Text(DropoffContact, nameof(DropoffContact));
    }
}
