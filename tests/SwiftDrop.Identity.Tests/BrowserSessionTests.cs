using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Npgsql;

using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Tests.Persistence;

namespace SwiftDrop.Identity.Tests;

[Collection(IdentityPostgresCollection.Name)]
public sealed class BrowserSessionTests(IdentityPostgresFixture database)
{
    private const string Password = "Correct Horse Battery 7!";

    [Fact]
    public async Task BootstrapUsesSecureHostCookiesAndRejectsUnlistedOrigins()
    {
        using var factory = new IdentityApiFactory();
        using var client = factory.Client();
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Equal(2, cookies.Length);
        foreach (var cookie in cookies)
        {
            Assert.StartsWith("__Host-", cookie);
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/", cookie);
        }

        client.DefaultRequestHeaders.Add("Origin", "https://attacker.example.test");
        using var rejected = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.False(rejected.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task BrowserRefreshRotatesHttpOnlyCookieAndNeverReturnsBodyRefreshToken()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client(cookies: true);
        var email = await RegisterAsync(client);
        await BootstrapAsync(client);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var firstCookie = RefreshCookie(login);
        Assert.Contains("path=/api/v1/auth", firstCookie);
        Assert.Contains("secure", firstCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", firstCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", firstCookie, StringComparison.OrdinalIgnoreCase);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        Assert.False(loginJson.RootElement.TryGetProperty("refreshToken", out _));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            loginJson.RootElement.GetProperty("accessToken").GetString());

        using var refreshed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.NotEqual(firstCookie, RefreshCookie(refreshed));
        using var refreshedJson = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
        Assert.False(refreshedJson.RootElement.TryGetProperty("refreshToken", out _));

        using var logout = await client.PostAsJsonAsync("/api/v1/auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("expires=Thu, 01 Jan 1970", RefreshCookie(logout), StringComparison.OrdinalIgnoreCase);
        using var afterLogout = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { });
        Assert.NotEqual(HttpStatusCode.OK, afterLogout.StatusCode);
    }

    [Theory]
    [InlineData("missing-csrf")]
    [InlineData("wrong-csrf")]
    [InlineData("wrong-origin")]
    [InlineData("missing-origin")]
    public async Task BrowserLoginRejectsInvalidOriginOrCsrfBeforeSessionMutation(string failure)
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client(cookies: true);
        var email = await RegisterAsync(client);
        await BootstrapAsync(client);
        if (failure == "missing-csrf") client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        if (failure == "wrong-csrf")
        {
            client.DefaultRequestHeaders.Remove("X-CSRF-Token");
            client.DefaultRequestHeaders.Add("X-CSRF-Token", "invalid");
        }
        if (failure is "wrong-origin" or "missing-origin") client.DefaultRequestHeaders.Remove("Origin");
        if (failure == "wrong-origin") client.DefaultRequestHeaders.Add("Origin", "https://attacker.example.test");
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await SessionsForEmailAsync(email));
    }

    [Fact]
    public async Task BrowserCannotSelectBodyTransportToReceiveARefreshToken()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client(cookies: true);
        var email = await RegisterAsync(client);
        await BootstrapAsync(client);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var mixed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = new string('a', 43) });
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        using var normal = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { });
        Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
    }

    [Fact]
    public async Task RefreshCookieFromAnotherBrowserCannotReplaceTheCurrentBrowserSession()
    {
        using var factory = new IdentityApiFactory(database);
        using var victim = factory.Client(cookies: true);
        using var attacker = factory.Client(cookies: true);
        var victimEmail = await RegisterAsync(victim);
        var attackerEmail = await RegisterAsync(attacker);
        victim.DefaultRequestHeaders.Add("Origin", IdentityApiFactory.Origin);
        using var bootstrap = await victim.GetAsync("/api/v1/auth/csrf");
        using var challenge = JsonDocument.Parse(await bootstrap.Content.ReadAsStringAsync());
        var csrf = challenge.RootElement.GetProperty("csrfToken").GetString();
        var hostCookie = Assert.Single(bootstrap.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("__Host-swiftdrop.csrf=", StringComparison.Ordinal)).Split(';')[0];
        victim.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        using var victimLogin = await victim.PostAsJsonAsync("/api/v1/auth/login", new { email = victimEmail, password = Password });
        Assert.Equal(HttpStatusCode.OK, victimLogin.StatusCode);
        await BootstrapAsync(attacker);
        using var attackerLogin = await attacker.PostAsJsonAsync("/api/v1/auth/login", new { email = attackerEmail, password = Password });
        Assert.Equal(HttpStatusCode.OK, attackerLogin.StatusCode);

        // A domain cookie from a sibling host must not replace the host's
        // browser session even when the real browser supplies its valid CSRF proof.
        using var substituted = factory.Client();
        substituted.DefaultRequestHeaders.Add("Origin", IdentityApiFactory.Origin);
        substituted.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        substituted.DefaultRequestHeaders.Add("Cookie", hostCookie + "; " + RefreshCookie(attackerLogin).Split(';')[0]);
        using var denied = await substituted.PostAsJsonAsync("/api/v1/auth/refresh", new { });
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal(1, await SessionsForEmailAsync(victimEmail));
        Assert.Equal(1, await SessionsForEmailAsync(attackerEmail));

        using var valid = await victim.PostAsJsonAsync("/api/v1/auth/refresh", new { });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var own = JsonDocument.Parse(await valid.Content.ReadAsStringAsync());
        Assert.Equal(victimEmail, own.RootElement.GetProperty("user").GetProperty("email").GetString());
    }

    [Theory]
    [InlineData("match", HttpStatusCode.OK)]
    [InlineData("mismatch", HttpStatusCode.Unauthorized)]
    [InlineData("missing", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    public async Task BrowserGoogleRequiresTheServerIssuedNonce(string mode, HttpStatusCode expected)
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client(cookies: true);
        var nonce = await BootstrapAsync(client);
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        factory.Google.Tokens["verified-google"] = new VerifiedGoogleIdentity(Guid.NewGuid().ToString("N"), email,
            "Fictional Google Customer", mode == "missing" ? null : mode == "mismatch" ? "another-nonce" : nonce);
        if (mode == "expired") factory.Clock.Now = factory.Clock.Now.AddMinutes(11);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = "verified-google" });
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, await SessionsForEmailAsync(email));
        if (expected == HttpStatusCode.OK)
        {
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(result.RootElement.TryGetProperty("refreshToken", out _));
            Assert.Contains("httponly", RefreshCookie(response), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task BrowserGoogleLinkRequiresFreshPasswordAndNonceWithoutChangingRoles()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client(cookies: true);
        var email = await RegisterAsync(client);
        await BootstrapAsync(client);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            loginJson.RootElement.GetProperty("accessToken").GetString());
        var nonce = await BootstrapAsync(client);
        factory.Google.Tokens["link-google"] = new VerifiedGoogleIdentity(Guid.NewGuid().ToString("N"), email, "Fictional", nonce);
        using var linked = await client.PostAsJsonAsync("/api/v1/auth/google/link", new
        {
            idToken = "link-google", currentPassword = Password
        });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        using var profile = JsonDocument.Parse(await linked.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "Customer" }, profile.RootElement.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        Assert.Equal(loginJson.RootElement.GetProperty("user").GetProperty("id").GetGuid(), profile.RootElement.GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData(IdentityApiFactory.Origin, true)]
    [InlineData("https://attacker.example.test", false)]
    public async Task CorsAllowsOnlyTheConfiguredOrigin(string origin, bool allowed)
    {
        using var factory = new IdentityApiFactory();
        using var client = factory.Client();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/refresh");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-csrf-token");
        using var response = await client.SendAsync(request);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed)
        {
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
        }
    }

    private static async Task<string> RegisterAsync(HttpClient client)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, fullName = "Fictional Customer" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return email;
    }

    private static async Task<string> BootstrapAsync(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", IdentityApiFactory.Origin);
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", body.RootElement.GetProperty("csrfToken").GetString());
        return body.RootElement.GetProperty("googleNonce").GetString()!;
    }

    private static string RefreshCookie(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("__Secure-swiftdrop.refresh=", StringComparison.Ordinal));

    private async Task<int> SessionsForEmailAsync(string email)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*)::int FROM refresh_sessions s JOIN users u ON u.id=s.user_id WHERE u.normalized_email=@email", connection);
        command.Parameters.AddWithValue("email", email.ToUpperInvariant());
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
