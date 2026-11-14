namespace SwiftDrop.Merchant.Api.Application.Merchants;

// ── Input contracts ────────────────────────────────────────────────────────────

/// <summary>Input for merchant self-registration.</summary>
public sealed record RegisterMerchantInput(
    string? BusinessName,
    string? ContactEmail,
    string? ContactPhone,
    string? PickupAddress);

/// <summary>Input for admin status transitions (Approve / Reject / Suspend).</summary>
public sealed record SetMerchantStatusInput(
    string? Status,
    string? Reason);

/// <summary>Input for API key creation.</summary>
public sealed record CreateApiKeyInput(
    IReadOnlyList<string>? Scopes);

// ── Output contracts ────────────────────────────────────────────────────────────

/// <summary>Merchant business record safe for API responses (no secrets).</summary>
public sealed record BusinessResponse(
    Guid Id,
    Guid UserId,
    string BusinessName,
    string ContactEmail,
    string ContactPhone,
    string PickupAddress,
    string Status,
    string? RejectionReason,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy);

/// <summary>
/// API key metadata — KeyHash and Salt are intentionally omitted.
/// Shown for list / revoke confirmation responses.
/// </summary>
public sealed record ApiKeyResponse(
    Guid Id,
    Guid BusinessId,
    string KeyPrefix,
    IReadOnlyList<string> Scopes,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt);

/// <summary>
/// Returned ONLY at creation time. Contains the plaintext key once.
/// KeyHash and Salt are intentionally omitted.
/// </summary>
public sealed record OneTimeApiKeyResponse(
    Guid KeyId,
    string ApiKey,
    IReadOnlyList<string> Scopes);
