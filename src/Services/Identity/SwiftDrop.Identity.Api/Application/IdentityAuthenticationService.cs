using System.Security.Cryptography;
using System.Text;

namespace SwiftDrop.Identity.Api.Application;

public sealed record IdentityAuthenticationResult(RegisteredProfile? User, string? AccessToken = null,
    string? RefreshToken = null, DateTimeOffset? RefreshExpiresAt = null, IdentityFailure? Failure = null);

public sealed class IdentityAuthenticationService(Func<IIdentityAuthenticationStore> storeFactory,
    IIdentityPasswordVerifier passwords, IIdentityAccessTokens accessTokens,
    IGoogleIdentityVerifier google, TimeProvider clock)
{
    private IIdentityAuthenticationStore Store => storeFactory();
    // An unknown account performs the same bounded password derivation. These
    // random bytes are not credentials and cannot authenticate an absent user.
    private static readonly string DummyHash = "argon2id$v=19$m=65536,t=3,p=1$"
        + Encode(RandomNumberGenerator.GetBytes(16)) + "$" + Encode(RandomNumberGenerator.GetBytes(32));

    public async Task<IdentityAuthenticationResult> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var account = await Store.FindByEmailAsync(email.Trim().ToUpperInvariant(), cancellationToken);
        var validPassword = await passwords.VerifyAsync(password, account?.PasswordHash ?? DummyHash, cancellationToken);
        var now = Now();
        if (account is null || !account.Profile.IsActive || account.LockedUntil > now || !validPassword
            || account.PasswordHash is null)
        {
            if (account is { Profile.IsActive: true } && account.LockedUntil <= now ||
                account is { Profile.IsActive: true, LockedUntil: null })
            {
                await Store.RecordLoginFailureAsync(account.Profile.Id, now, cancellationToken);
            }

            return Failed();
        }

        accessTokens.EnsureConfigured();
        var (raw, session) = NewSession(now);
        var result = await Store.CreateSessionAsync(account.Profile.Id, session, cancellationToken, account.PasswordHash);
        return Complete(result, raw, now);
    }

    public async Task<IdentityAuthenticationResult> RefreshAsync(string token, CancellationToken cancellationToken)
    {
        if (!ValidRefreshToken(token))
        {
            return Failed();
        }

        accessTokens.EnsureConfigured();
        var now = Now();
        var (raw, replacement) = NewSession(now);
        return Complete(await Store.RotateAsync(Hash(token), replacement, cancellationToken), raw, now);
    }

    public Task<bool> LogoutAsync(Guid userId, string token, CancellationToken cancellationToken) =>
        ValidRefreshToken(token)
            ? Store.LogoutAsync(userId, Hash(token), Now(), cancellationToken)
            : Task.FromResult(false);

    public async Task<IdentityAuthenticationResult> GoogleAsync(string idToken, string? expectedNonce,
        bool requireNonce, CancellationToken cancellationToken)
    {
        var identity = await VerifyGoogleAsync(idToken, expectedNonce, requireNonce, cancellationToken);
        if (identity is null)
        {
            return Failed();
        }

        accessTokens.EnsureConfigured();
        var now = Now();
        var (raw, session) = NewSession(now);
        return Complete(await Store.AuthenticateGoogleAsync(identity, session, cancellationToken), raw, now);
    }

    public async Task<IdentityAuthenticationResult> LinkGoogleAsync(Guid userId, string idToken, string currentPassword,
        string? expectedNonce, bool requireNonce, CancellationToken cancellationToken)
    {
        var account = await Store.FindByIdAsync(userId, cancellationToken);
        var validPassword = await passwords.VerifyAsync(currentPassword, account?.PasswordHash ?? DummyHash, cancellationToken);
        var now = Now();
        if (account is null || !account.Profile.IsActive || account.PasswordHash is null
            || account.LockedUntil > now || !validPassword)
        {
            if (account is { Profile.IsActive: true, LockedUntil: null } ||
                account is { Profile.IsActive: true } && account.LockedUntil <= now)
            {
                await Store.RecordLoginFailureAsync(userId, now, cancellationToken);
            }

            return Failed();
        }

        var identity = await VerifyGoogleAsync(idToken, expectedNonce, requireNonce, cancellationToken);
        if (identity is null)
        {
            return Failed();
        }

        var result = await Store.LinkGoogleAsync(userId, account.PasswordHash, identity, now, cancellationToken);
        return result.Account is { } linked
            ? new(linked.Profile)
            : Failed(result.Failure ?? IdentityFailure.InvalidCredentials);
    }

    private async Task<VerifiedGoogleIdentity?> VerifyGoogleAsync(string idToken, string? expectedNonce,
        bool requireNonce, CancellationToken cancellationToken)
    {
        var identity = await google.VerifyAsync(idToken, cancellationToken);
        if (identity is null || requireNonce && string.IsNullOrEmpty(expectedNonce))
        {
            return null;
        }

        // A nonce-bearing token must match a server-issued challenge. Mobile
        // SDK flows without a nonce remain valid; client-supplied nonce values
        // are never accepted as an independent ownership proof.
        if (identity.Nonce is not null || expectedNonce is not null)
        {
            if (identity.Nonce is null || expectedNonce is null ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(identity.Nonce), Encoding.UTF8.GetBytes(expectedNonce)))
            {
                return null;
            }
        }

        return identity;
    }

    private IdentityAuthenticationResult Complete(IdentityStoreResult result, string rawToken, DateTimeOffset now) =>
        result.Account is { } account
            ? new(account.Profile, accessTokens.Issue(account.Profile, now), rawToken, result.SessionExpiresAt)
            : Failed(result.Failure ?? IdentityFailure.InvalidCredentials);

    private static IdentityAuthenticationResult Failed(IdentityFailure failure = IdentityFailure.InvalidCredentials) =>
        new(null, Failure: failure);

    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }

    private static (string Raw, RefreshSessionWrite Session) NewSession(DateTimeOffset now)
    {
        var token = Encode(RandomNumberGenerator.GetBytes(32));
        return (token, new(Guid.NewGuid(), Hash(token), now, now.AddDays(30)));
    }

    private static bool ValidRefreshToken(string token) => token.Length == 43
        && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
