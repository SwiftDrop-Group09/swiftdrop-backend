namespace SwiftDrop.Identity.Api.Application;

public enum IdentityAdministrationFailure
{
    Forbidden,
    NotFound,
    Conflict,
    LastAdmin,
    InvalidRoles
}

public sealed record IdentityAdministrationResult<T>(T? Value, IdentityAdministrationFailure? Failure = null);
public sealed record ProfileUpdate(string FullName, string? PhoneNumber, string? Address);
public sealed record IdentityPage<T>(IReadOnlyList<T> Items, long TotalCount, int Page, int PageSize);
public sealed record IdentityAuditRecord(long Id, Guid? ActorId, string Action, Guid? ResourceId,
    DateTimeOffset Timestamp, string ResultCode);

public interface IIdentityAdministrationStore
{
    Task<IdentityAdministrationResult<RegisteredProfile>> GetProfileAsync(Guid actor, CancellationToken cancellationToken);
    Task<IdentityAdministrationResult<RegisteredProfile>> UpdateProfileAsync(Guid actor, ProfileUpdate input,
        DateTimeOffset now, string? correlation, CancellationToken cancellationToken);
    Task<IdentityAdministrationResult<IdentityPage<RegisteredProfile>>> ListUsersAsync(Guid actor, int page,
        int pageSize, CancellationToken cancellationToken);
    Task<IdentityAdministrationResult<RegisteredProfile>> CreateUserAsync(Guid actor, string email,
        string normalizedEmail, string fullName, bool isActive, string passwordHash, DateTimeOffset now,
        string? correlation, CancellationToken cancellationToken);
    Task<IdentityAdministrationResult<RegisteredProfile>> UpdateUserAsync(Guid actor, Guid target, string email,
        string normalizedEmail, string fullName, bool isActive, DateTimeOffset now, string? correlation,
        CancellationToken cancellationToken);
    Task<IdentityAdministrationResult<RegisteredProfile>> SetRolesAsync(Guid actor, Guid target, string[] roles,
        DateTimeOffset now, string? correlation, CancellationToken cancellationToken);
    Task<IdentityAdministrationResult<bool>> DeactivateUserAsync(Guid actor, Guid target, DateTimeOffset now,
        string? correlation, CancellationToken cancellationToken);
    Task<IdentityAdministrationResult<IdentityPage<IdentityAuditRecord>>> ListAuditAsync(Guid actor, int page,
        int pageSize, CancellationToken cancellationToken);
}
