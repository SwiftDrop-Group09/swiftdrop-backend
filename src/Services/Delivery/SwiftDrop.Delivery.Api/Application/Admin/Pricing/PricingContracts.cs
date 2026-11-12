using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Admin.Pricing;

public enum PricingFailure
{
    Unauthenticated,
    Forbidden,
    NotFound,
    Conflict,
    InvalidInput
}

public sealed record PricingTierInput(
    Guid ZoneId,
    string Size,
    string PricingVersion,
    decimal BaseRate,
    decimal PerKgRate,
    string Currency = "LKR",
    bool IsActive = true,
    long? ExpectedVersion = null);

public sealed record PricingTierUpdateInput(
    bool IsActive,
    long? ExpectedVersion = null);

public sealed record PricingTierDto(
    Guid Id,
    Guid ZoneId,
    string ZoneName,
    string Size,
    string PricingVersion,
    decimal BaseRate,
    decimal PerKgRate,
    string Currency,
    bool IsActive,
    long Version = 1);

public sealed record PricingPage(
    IReadOnlyList<PricingTierDto> Items,
    long TotalCount,
    int Page,
    int PageSize);

public sealed record PricingResult<T>(T? Response, PricingFailure? Failure = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
