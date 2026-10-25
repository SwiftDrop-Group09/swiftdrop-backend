using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;

namespace SwiftDrop.Identity.Api.Presentation;

public sealed class BrowserAuthentication(IAntiforgery antiforgery, IDataProtectionProvider protection,
    IConfiguration configuration, TimeProvider clock)
{
    public const string RefreshCookie = "__Secure-swiftdrop.refresh";
    public const string CsrfCookie = "__Host-swiftdrop.csrf";
    private const string NonceCookie = "__Host-swiftdrop.google-nonce";
    private readonly IDataProtector nonceProtector = protection.CreateProtector("SwiftDrop.GoogleNonce.v1");
    private readonly IDataProtector refreshProtector = protection.CreateProtector("SwiftDrop.BrowserRefresh.v1");

    public static bool IsBrowser(HttpContext context) => context.Request.Headers.ContainsKey("Origin")
        || context.Request.Headers.ContainsKey("Sec-Fetch-Site") || context.Request.Cookies.Count > 0;

    public async Task<bool> ValidateAsync(HttpContext context)
    {
        var origins = configuration.GetSection("Identity:Browser:AllowedOrigins").Get<string[]>() ?? [];
        var supplied = context.Request.Headers.Origin;
        if (supplied.Count != 1 || !ValidOrigin(supplied[0])
            || !origins.Contains(supplied[0], StringComparer.Ordinal))
        {
            return false;
        }

        var user = context.User;
        try
        {
            // Bind CSRF to the protected host cookie, independently of an access
            // token that may expire before a refresh request. Authentication and
            // refresh-family ownership are validated separately.
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
        finally
        {
            context.User = user;
        }
    }

    public IResult Bootstrap(HttpContext context)
    {
        var origins = configuration.GetSection("Identity:Browser:AllowedOrigins").Get<string[]>() ?? [];
        var supplied = context.Request.Headers.Origin;
        var requestOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (supplied.Count == 0 && fetchSite.Length > 0 && fetchSite is not ("same-origin" or "none")
            || supplied.Count > 1 || supplied.Count == 1 && !origins.Contains(supplied[0], StringComparer.Ordinal)
            || supplied.Count == 0 && !origins.Contains(requestOrigin, StringComparer.Ordinal)
            || origins.Any(origin => !ValidOrigin(origin)))
        {
            return IdentityEndpoints.Problem(403, "csrf_rejected", "Browser request verification failed.");
        }

        var user = context.User;
        AntiforgeryTokenSet tokens;
        try
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            tokens = antiforgery.GetAndStoreTokens(context);
        }
        finally
        {
            context.User = user;
        }

        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expires = clock.GetUtcNow().AddMinutes(10);
        context.Response.Cookies.Append(NonceCookie,
            nonceProtector.Protect(nonce + ":" + expires.ToUnixTimeSeconds()), Cookie(TimeSpan.FromMinutes(10), "/"));
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { csrfToken = tokens.RequestToken, googleNonce = nonce });
    }

    public string? ExpectedGoogleNonce(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(NonceCookie, out var encrypted))
        {
            return null;
        }

        try
        {
            var values = nonceProtector.Unprotect(encrypted).Split(':');
            return values.Length == 2 && values[0].Length == 64 && long.TryParse(values[1], out var expiry)
                && clock.GetUtcNow().ToUnixTimeSeconds() < expiry ? values[0] : null;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public void SetRefresh(HttpContext context, string token, DateTimeOffset expiresAt)
    {
        if (!context.Request.Cookies.TryGetValue(CsrfCookie, out var csrfCookie))
        {
            throw new InvalidOperationException("The browser session context is unavailable.");
        }

        var options = Cookie(null, "/api/v1/auth");
        options.Expires = expiresAt;
        var binding = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(csrfCookie)));
        context.Response.Cookies.Append(RefreshCookie, refreshProtector.Protect(token + ":" + binding), options);
    }

    public string? ReadRefresh(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(RefreshCookie, out var encrypted)
            || !context.Request.Cookies.TryGetValue(CsrfCookie, out var csrfCookie))
        {
            return null;
        }

        try
        {
            var parts = refreshProtector.Unprotect(encrypted).Split(':');
            if (parts.Length != 2 || parts[0].Length != 43 || parts[1].Length != 64)
            {
                return null;
            }

            var expected = SHA256.HashData(Encoding.UTF8.GetBytes(csrfCookie));
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(parts[1])) ? parts[0] : null;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public static void ClearRefresh(HttpContext context) =>
        context.Response.Cookies.Delete(RefreshCookie, Cookie(null, "/api/v1/auth"));

    public static void ClearGoogleNonce(HttpContext context) =>
        context.Response.Cookies.Delete(NonceCookie, Cookie(null, "/"));

    public static bool ValidOrigin(string? origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && origin == uri.GetLeftPart(UriPartial.Authority)
        && (uri.Scheme == "https" || uri.IsLoopback);

    private static CookieOptions Cookie(TimeSpan? age, string path) => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = path,
        MaxAge = age, IsEssential = true
    };
}
