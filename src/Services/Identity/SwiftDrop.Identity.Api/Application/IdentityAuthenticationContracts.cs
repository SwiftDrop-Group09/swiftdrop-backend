namespace SwiftDrop.Identity.Api.Application;

public sealed record IdentityAccount(RegisteredProfile Profile, string? PasswordHash, DateTimeOffset? LockedUntil);

public sealed record RefreshSessionWrite(Guid Id, string TokenHash, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record VerifiedGoogleIdentity(string Subject, string Email, string FullName, string? Nonce);

public enum IdentityFailure
{
    InvalidCredentials,
    AccountLinkRequired,
    SubjectConflict,
    EmailMismatch
}

public sealed record IdentityStoreResult(IdentityAccount? Account, IdentityFailure? Failure = null,
    DateTimeOffset? SessionExpiresAt = null);

public interface IIdentityAuthenticationStore
{
    Task<IdentityAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);
    Task<IdentityAccount?> FindByIdAsync(Guid userId, CancellationToken cancellationToken);
    Task RecordLoginFailureAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IdentityStoreResult> CreateSessionAsync(Guid userId, RefreshSessionWrite session, CancellationToken cancellationToken,
        string? expectedPasswordHash = null);
    Task<IdentityStoreResult> RotateAsync(string tokenHash, RefreshSessionWrite replacement,
        CancellationToken cancellationToken);
    Task<bool> LogoutAsync(Guid userId, string tokenHash, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IdentityStoreResult> AuthenticateGoogleAsync(VerifiedGoogleIdentity identity,
        RefreshSessionWrite session, CancellationToken cancellationToken);
    Task<IdentityStoreResult> LinkGoogleAsync(Guid userId, string expectedPasswordHash,
        VerifiedGoogleIdentity identity, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IIdentityPasswordVerifier
{
    Task<bool> VerifyAsync(string password, string? encodedHash, CancellationToken cancellationToken);
}

public interface IIdentityAccessTokens
{
    void EnsureConfigured();
    string Issue(RegisteredProfile user, DateTimeOffset now);
    object PublicKeys();
}

public interface IGoogleIdentityVerifier
{
    Task<VerifiedGoogleIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken);
}
