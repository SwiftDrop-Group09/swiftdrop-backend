namespace SwiftDrop.Identity.Api.Application;

public sealed record RegisteredProfile(
    Guid Id,
    string Email,
    string FullName,
    string? PhoneNumber,
    string? Address,
    IReadOnlyList<string> Roles,
    bool IsActive);
