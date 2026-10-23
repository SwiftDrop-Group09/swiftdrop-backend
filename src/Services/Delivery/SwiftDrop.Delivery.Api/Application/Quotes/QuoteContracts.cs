using System.Text.Json.Serialization;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Quotes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QuoteInput(
    AddressInput? PickupAddress,
    AddressInput? DeliveryAddress,
    PackageDetailsInput? PackageDetails,
    RecipientInput? Recipient);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddressInput(string? Street, string? City, string? PostalCode, string? ZoneId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageDetailsInput(decimal? WeightKg, string? Size);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipientInput(string? Name, string? PhoneNumber);

public sealed record NormalizedAddress(string Street, string City, string PostalCode, Guid ZoneId);
public sealed record NormalizedPackageDetails(decimal WeightKg, ParcelSize Size);
public sealed record NormalizedRecipient(string Name, string PhoneNumber);
public sealed record NormalizedQuoteInput(
    NormalizedAddress PickupAddress,
    NormalizedAddress DeliveryAddress,
    NormalizedPackageDetails PackageDetails,
    NormalizedRecipient Recipient);

public sealed record QuoteResponse(
    Guid QuoteId,
    decimal Total,
    string Currency,
    string PricingVersion,
    DateTimeOffset ExpiresAt,
    string Fingerprint);

public enum QuoteFailure
{
    Unauthenticated,
    Forbidden,
    InvalidInput,
    UnsupportedZone,
    PricingUnavailable,
    PersistenceUnavailable
}

public sealed record QuoteEstimationResult(
    QuoteResponse? Quote,
    QuoteFailure? Failure,
    IReadOnlyDictionary<string, string[]>? Errors = null);
