using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Api.Infrastructure.Authentication.ApiKeys;

/// <summary>
/// Authenticates merchant systems by <c>X-API-Key</c>.
/// <list type="bullet">
/// <item>No header → no result (other schemes may still authenticate).</item>
/// <item>Malformed, unknown, wrong-secret or revoked key → 401.</item>
/// <item>Valid key → identity with business/key/scope claims and no <c>sub</c>.</item>
/// </list>
/// Business approval is not decided here: it is checked per request by the
/// order policies, so a valid key of a suspended business gets 403, not 401.
/// The raw key is never logged or placed in a failure message.
/// <para>The handler is created for every request that evaluates this scheme
/// (including requests without a key), so the database is resolved only once a
/// key is actually presented.</para>
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string InvalidKey = "The API key is invalid or revoked.";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationDefaults.HeaderName, out var values) || values.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        if (values.Count != 1)
        {
            return AuthenticateResult.Fail("Exactly one API key header is allowed.");
        }

        // A request must carry one identity; mixing a bearer token and a key is ambiguous.
        if (Request.Headers.ContainsKey("Authorization"))
        {
            return AuthenticateResult.Fail("Send either an Authorization header or an API key, not both.");
        }

        var presented = values[0];
        if (!ApiKeyParser.TryParse(presented, out var parsed))
        {
            return AuthenticateResult.Fail(InvalidKey);
        }

        var key = await FindVerifiedKeyAsync(parsed, presented!, Context.RequestAborted);
        return key is null
            ? AuthenticateResult.Fail(InvalidKey)
            : AuthenticateResult.Success(new AuthenticationTicket(CreatePrincipal(key), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "ApiKey";
        return base.HandleChallengeAsync(properties);
    }

    /// <summary>Returns the key only when it exists, is active and the secret verifies (constant time).</summary>
    private async Task<ApiKey?> FindVerifiedKeyAsync(ParsedApiKey parsed, string presented, CancellationToken cancellationToken)
    {
        var database = Context.RequestServices.GetRequiredService<MerchantDbContext>();
        var candidates = database.ApiKeys.AsNoTracking().Where(key => key.KeyPrefix == parsed.Prefix);
        if (parsed.KeyId is { } keyId)
        {
            candidates = candidates.Where(key => key.Id == keyId);
        }

        // Current keys match at most one row by primary key; legacy 8-hex prefixes may collide, so verify each.
        var matches = await candidates.Take(5).ToListAsync(cancellationToken);
        return matches.FirstOrDefault(key => key.Verify(presented));
    }

    private ClaimsPrincipal CreatePrincipal(ApiKey key)
    {
        var claims = new List<Claim>
        {
            new(ApiKeyClaimTypes.BusinessId, key.BusinessId.ToString("D")),
            new(ApiKeyClaimTypes.ApiKeyId, key.Id.ToString("D")),
            new(ApiKeyClaimTypes.AuthenticationMethod, ApiKeyAuthenticationDefaults.AuthenticationMethod)
        };
        claims.AddRange(key.GetScopesList().Select(scope => new Claim(ApiKeyClaimTypes.Scope, scope)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
    }
}
