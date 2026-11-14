using System.Text.Json.Serialization;

namespace SwiftDrop.Merchant.Api.Application.Orders;

// ── Input (MerchantOrderInput = externalOrderId + QuoteInput fields + acceptedPriceVersion) ──
// Unknown members are rejected, which also rejects caller-supplied
// merchantId / userId / role: the business always comes from the credential.

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MerchantOrderInput(
    string? ExternalOrderId,
    OrderAddressInput? PickupAddress,
    OrderAddressInput? DeliveryAddress,
    OrderPackageInput? PackageDetails,
    OrderRecipientInput? Recipient,
    string? AcceptedPriceVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderAddressInput(string? Street, string? City, string? PostalCode, string? ZoneId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderPackageInput(decimal? WeightKg, string? Size);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderRecipientInput(string? Name, string? PhoneNumber);

/// <summary>Raw query string values; parsed and validated by <see cref="OrderInputValidator"/>.</summary>
public sealed record OrderListQuery(string? Page, string? PageSize, string? State);

// ── Validated order, as stored in inbox_jobs.order_payload and replayed to Delivery ──

public sealed record MerchantOrderPayload(
    string ExternalOrderId,
    OrderAddress PickupAddress,
    OrderAddress DeliveryAddress,
    OrderPackage PackageDetails,
    OrderRecipient Recipient,
    string AcceptedPriceVersion);

public sealed record OrderAddress(string Street, string City, string PostalCode, Guid ZoneId);

/// <summary>Weight keeps the exact decimal the merchant sent (scale included); it is never rounded.</summary>
public sealed record OrderPackage(decimal WeightKg, string Size);

public sealed record OrderRecipient(string Name, string PhoneNumber);

public sealed record OrderPage(int Page, int PageSize, Domain.Orders.InboxJobState? State);

// ── Output ──

/// <summary>
/// OrderJob: parcel fields stay null until Delivery confirms the parcel —
/// the API never returns a placeholder tracking number.
/// </summary>
public sealed record OrderJobResponse(
    Guid Id,
    string ExternalOrderId,
    string State,
    int Attempts,
    DateTimeOffset? NextRetryAt,
    Guid? ParcelId,
    string? TrackingNumber,
    string? ParcelStatus,
    long? ParcelVersion,
    DateTimeOffset? LastStatusChangedAt,
    DateTimeOffset? TerminalAt,
    string? ErrorCode,
    DateTimeOffset? LastRefreshedAt,
    DateTimeOffset CreatedAt);

public sealed record OrderJobPage(
    IReadOnlyList<OrderJobResponse> Items,
    int TotalCount,
    int Page,
    int PageSize);
