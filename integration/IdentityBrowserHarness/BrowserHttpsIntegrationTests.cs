using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Playwright;
using Microsoft.Extensions.FileProviders;

using SwiftDrop.Identity.Tests.Persistence;
using SwiftDrop.Identity.Api.Infrastructure.Authentication;
using SwiftDrop.Identity.Api.Infrastructure;

using Yarp.ReverseProxy.Configuration;

namespace SwiftDrop.Identity.BrowserHarness;

internal static class BrowserHttpsIntegrationTests
{
    private const string Password = "Fictional browser harness password 74!";
    private const string RefreshCookieName = "__Secure-swiftdrop.refresh";
    private static readonly IPAddress ProxyAddress = IPAddress.Parse("127.0.0.2");
    private static int passed;

    public static async Task RunAsync(string? frontendDist = null, string? mobileProject = null)
    {
        Environment.SetEnvironmentVariable("DEBUG", null);
        Environment.SetEnvironmentVariable("PWDEBUG", null);
        if (frontendDist is not null && (!File.Exists(Path.Combine(frontendDist, "index.html"))
            || !Directory.Exists(Path.Combine(frontendDist, "assets"))))
            throw new InvalidOperationException("Build the frontend SPA before selecting its client output directory.");
        if (mobileProject is not null && !File.Exists(Path.Combine(mobileProject, "integration", "identity_api_smoke.dart")))
            throw new InvalidOperationException("The standalone Flutter Identity integration fixture is required.");
        // Validate the browser prerequisite before starting any application or DB.
        var channel = Environment.GetEnvironmentVariable("SWIFTDROP_BROWSER_CHANNEL") ?? "msedge";
        if (channel is not ("msedge" or "chrome" or "chromium"))
            throw new InvalidOperationException("Select msedge, chrome or chromium.");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = channel, Headless = true
        });
        Console.WriteLine($"Browser: {channel} {browser.Version}");

        var parent = Path.GetFullPath(Path.GetTempPath());
        var owned = Path.Combine(parent, "swiftdrop-identity-browser-" + Guid.NewGuid().ToString("N"));
        EnsureOwnedDirectory(owned, parent);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(owned);
        else Directory.CreateDirectory(owned, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = new IdentityPostgresFixture();
        try
        {
            await database.InitializeAsync();
            Console.WriteLine("Owned PostgreSQL is ready.");
            using var signing = RSA.Create(2048);
            var signingPath = Path.Combine(owned, "signing.pem");
            var signingOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) signingOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var writer = new StreamWriter(new FileStream(signingPath, signingOptions)))
                await writer.WriteAsync(signing.ExportPkcs8PrivateKeyPem());
            using var certificate = CreateCertificate();
            var publicPort = ReservePort();
            var origin = $"https://localhost:{publicPort}";
            using var identity = new IdentityKestrelFactory(database, owned, signingPath, origin);
            identity.UseKestrel(0);
            Console.WriteLine("Starting real Identity Kestrel host.");
            identity.ClientOptions.HandleCookies = false;
            using var internalClient = identity.CreateClient();
            var identityAddress = internalClient.BaseAddress!.AbsoluteUri;

            var observations = new ConcurrentQueue<ProxyObservation>();
            await using var proxy = CreateHttpsProxy(certificate, publicPort, identityAddress, observations, frontendDist);
            await proxy.StartAsync();
            await using var attacker = CreateHttpsPage(certificate);
            await attacker.StartAsync();
            var attackerAddress = attacker.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            var attackerOrigin = new UriBuilder(attackerAddress) { Host = "localhost" }.Uri.GetLeftPart(UriPartial.Authority);

            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                // Limited to this ephemeral context/certificate. No trust-store
                // changes or application TLS bypass is made.
                IgnoreHTTPSErrors = true
            });
            context.SetDefaultTimeout(15000);
            var page = await context.NewPageAsync();
            await page.GotoAsync(origin + "/_identity-harness");
            Check(await page.EvaluateAsync<bool>("window.isSecureContext && location.protocol === 'https:'"),
                "real browser document uses HTTPS and a secure context");
            await page.EvaluateAsync(ClientScript);

            var bootstrapRequest = page.WaitForRequestAsync(request => request.Url == origin + "/api/v1/auth/csrf");
            var bootstrap = await Call(page, "/api/v1/auth/csrf", saveBootstrap: true);
            Check(Status(bootstrap) == 200 && !(await (await bootstrapRequest).AllHeadersAsync()).ContainsKey("origin"),
                "same-origin bootstrap without Origin succeeds through trusted public HTTPS forwarding");
            var csrfCookies = await context.CookiesAsync();
            Check(csrfCookies.Where(cookie => cookie.Name.StartsWith("__Host-swiftdrop.", StringComparison.Ordinal))
                .Count(cookie => cookie.Secure && cookie.HttpOnly && cookie.SameSite == SameSiteAttribute.Strict && cookie.Path == "/") == 2,
                "browser stores both host-prefixed nonce/CSRF cookies as Secure HttpOnly Strict");
            Check(!await page.EvaluateAsync<bool>("document.cookie.includes('swiftdrop')"),
                "HttpOnly Identity cookies are inaccessible to document JavaScript");

            var email = "browser-" + Guid.NewGuid().ToString("N") + "@example.test";
            var registered = await Call(page, "/api/v1/auth/register", "POST", new { email, password = Password, fullName = "Browser Fixture" });
            Check(Status(registered) == 201, "browser registration reaches real Identity and private PostgreSQL");
            var userId = registered.GetProperty("userId").GetGuid();
            var login = await Call(page, "/api/v1/auth/login", "POST", new { email, password = Password }, saveAccess: true);
            Check(Status(login) == 200 && !login.GetProperty("hasRefreshToken").GetBoolean(),
                "browser login returns an application access token without JSON refresh credentials");
            var refreshBefore = (await context.CookiesAsync()).Single(cookie => cookie.Name == RefreshCookieName);
            Check(refreshBefore.Secure && refreshBefore.HttpOnly && refreshBefore.SameSite == SameSiteAttribute.Strict
                && refreshBefore.Path == "/api/v1/auth" && refreshBefore.Domain == "localhost",
                "browser accepts the host-only Secure HttpOnly Strict auth-path refresh cookie");

            var profileRequest = page.WaitForRequestAsync(request => request.Url == origin + "/api/v1/users/me");
            var profile = await Call(page, "/api/v1/users/me", bearer: true);
            var sentProfileHeaders = await (await profileRequest).AllHeadersAsync();
            Check(Status(profile) == 200 && profile.GetProperty("userId").GetGuid() == userId,
                "profile derives the same verified user through real TLS proxy transport");
            Check(!sentProfileHeaders.GetValueOrDefault("cookie", "").Contains(RefreshCookieName, StringComparison.Ordinal),
                "browser enforces refresh-cookie path isolation on profile requests");

            var denied = await Call(page, "/api/v1/users/me", "PUT", new { fullName = "CSRF rejected", phoneNumber = "" }, bearer: true, csrf: false);
            Check(Status(denied) == 403, "browser mutation without CSRF proof is rejected");
            var updated = await Call(page, "/api/v1/users/me", "PUT", new { fullName = "TLS Updated", phoneNumber = "0771234567", address = "Fictional fixture" }, bearer: true);
            Check(Status(updated) == 200 && updated.GetProperty("fullName").GetString() == "TLS Updated",
                "CSRF-protected profile PUT succeeds through the public HTTPS proxy");

            var sessionCount = await SessionCount(database, userId);
            var attack = await context.NewPageAsync();
            await attack.GotoAsync(attackerOrigin);
            // Observe only safe metadata at the proxy. Browser JavaScript cannot
            // read a disallowed-origin response, which is expected CORS behavior.
            var blocked = await attack.EvaluateAsync<bool>("""
                async origin => {
                  try {
                    await fetch(origin + '/api/v1/auth/refresh', {
                      method: 'POST', credentials: 'include',
                      headers: { 'Content-Type': 'text/plain' }, body: '{}'
                    });
                    return false;
                  } catch { return true; }
                }
                """, origin);
            Check(blocked && observations.Any(item => item.Path == "/api/v1/auth/refresh"
                && item.Origin == attackerOrigin && item.Status == 403 && item.HasRefreshCookie)
                && await SessionCount(database, userId) == sessionCount,
                "different same-site browser Origin is denied despite attached cookies, without session mutation");

            await attack.GotoAsync(attackerAddress);
            var crossSiteBlocked = await attack.EvaluateAsync<bool>("""
                async origin => {
                  try {
                    await fetch(origin + '/api/v1/auth/refresh', {
                      method: 'POST', credentials: 'include', signal: AbortSignal.timeout(15000),
                      headers: { 'Content-Type': 'text/plain' }, body: '{}'
                    });
                    return false;
                  } catch { return true; }
                }
                """, origin);
            Check(crossSiteBlocked && observations.Any(item => item.Path == "/api/v1/auth/refresh"
                && item.Origin == attackerAddress.TrimEnd('/') && item.Status == 403 && !item.HasRefreshCookie)
                && await SessionCount(database, userId) == sessionCount,
                "browser enforces SameSite Strict on a cross-site HTTPS request and the server rejects its Origin");

            var forged = new HttpRequestMessage(HttpMethod.Get, "api/v1/auth/csrf");
            forged.Headers.Add("X-Forwarded-Host", new Uri(origin).Authority);
            forged.Headers.Add("X-Forwarded-Proto", "https");
            forged.Headers.Add("X-Forwarded-For", "203.0.113.20");
            using var direct = await internalClient.SendAsync(forged);
            Check(direct.StatusCode == HttpStatusCode.Forbidden,
                "direct untrusted loopback caller cannot forge public host/protocol forwarding");

            var forwarded = await Call(page, "/api/v1/auth/csrf", saveBootstrap: true,
                extraHeaders: new Dictionary<string, string>
                {
                    ["X-Forwarded-Host"] = "attacker.example.test", ["X-Forwarded-Proto"] = "http",
                    ["X-Forwarded-For"] = "203.0.113.99"
                });
            Check(Status(forwarded) == 200, "public proxy replaces caller-supplied forwarding headers with actual HTTPS metadata");
            var refreshed = await Call(page, "/api/v1/auth/refresh", "POST", new { }, saveAccess: true);
            var refreshAfter = (await context.CookiesAsync()).Single(cookie => cookie.Name == RefreshCookieName);
            Check(Status(refreshed) == 200 && !refreshed.GetProperty("hasRefreshToken").GetBoolean()
                && refreshAfter.Value != refreshBefore.Value,
                "real browser refresh rotates the protected cookie and omits JSON refresh credentials");

            var logout = await Call(page, "/api/v1/auth/logout", "POST", new { }, bearer: true);
            Check(Status(logout) == 204 && (await context.CookiesAsync()).All(cookie => cookie.Name != RefreshCookieName),
                "browser logout revokes the family and deletes the auth-path refresh cookie");
            Check(await LiveFamilyCount(database, userId) == 0, "logout leaves no live refresh family in PostgreSQL");
            Check(Status(await Call(page, "/api/v1/auth/refresh", "POST", new { })) == 400,
                "refresh after browser cookie deletion fails closed");
            Check(Status(await Call(page, "/api/v1/users/me", bearer: true)) == 200,
                "already-issued access JWT retains its documented expiry behavior after logout");
            await context.ClearCookiesAsync();
            if (frontendDist is not null)
            {
                await ReactFlow(browser, origin, observations);
                await ReactAdminFlow(browser, origin, database, owned);
            }
            if (mobileProject is not null) await FlutterFlow(mobileProject, origin, certificate, owned);
            Console.WriteLine($"PASS: {passed} HTTPS browser integration assertions; no live Google, Neon or production deployment claimed.");
        }
        finally
        {
            await database.DisposeAsync();
            EnsureOwnedDirectory(owned, parent);
            if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
        }
    }

    private static async Task<JsonElement> Call(IPage page, string path, string method = "GET", object? body = null,
        bool bearer = false, bool csrf = true, bool saveAccess = false, bool saveBootstrap = false,
        Dictionary<string, string>? extraHeaders = null)
    {
        var result = await page.EvaluateAsync<JsonElement>(
            "options => window.identityHarness.call(options)", new { path, method, body, bearer, csrf, saveAccess, saveBootstrap, extraHeaders });
        Console.WriteLine($"HTTP {method} {path}: {Status(result)}");
        return result;
    }

    private static int Status(JsonElement result) => result.GetProperty("status").GetInt32();

    private static async Task<long> SessionCount(IdentityPostgresFixture database, Guid userId)
    {
        await using var query = database.DataSource.CreateCommand("SELECT COUNT(*) FROM public.refresh_sessions WHERE user_id=@id");
        query.Parameters.AddWithValue("id", userId);
        return (long)(await query.ExecuteScalarAsync())!;
    }

    private static async Task<long> LiveFamilyCount(IdentityPostgresFixture database, Guid userId)
    {
        await using var query = database.DataSource.CreateCommand("SELECT COUNT(*) FROM public.refresh_families WHERE user_id=@id AND revoked_at IS NULL");
        query.Parameters.AddWithValue("id", userId);
        return (long)(await query.ExecuteScalarAsync())!;
    }

    private static void Check(bool condition, string name)
    {
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {name}");
        if (!condition) throw new InvalidOperationException("Named integration assertion failed.");
        passed++;
    }

    private static WebApplication CreateHttpsProxy(X509Certificate2 certificate, int port, string identityAddress,
        ConcurrentQueue<ProxyObservation> observations, string? frontendDist)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
        builder.Services.AddReverseProxy().LoadFromMemory(
            [new RouteConfig { RouteId = "identity", ClusterId = "identity", Match = new RouteMatch { Path = "/api/{**rest}" },
                Transforms = [new Dictionary<string, string> { ["X-Forwarded"] = "Set" }] }],
            [new ClusterConfig { ClusterId = "identity", Destinations = new Dictionary<string, DestinationConfig>
                { ["identity"] = new() { Address = identityAddress } } }])
            .ConfigureHttpClient((_, handler) => handler.ConnectCallback = async (connection, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(ProxyAddress, 0));
                    await socket.ConnectAsync(connection.DnsEndPoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            await next(context);
            if (context.Request.Path.StartsWithSegments("/api"))
                observations.Enqueue(new(context.Request.Path.Value!, context.Request.Headers.Origin.ToString(), context.Response.StatusCode,
                    context.Request.Cookies.ContainsKey(RefreshCookieName)));
        });
        app.MapGet("/_identity-harness", () => Results.Content("<!doctype html><title>Identity TLS integration fixture</title>", "text/html"));
        if (frontendDist is not null)
        {
            var files = new PhysicalFileProvider(frontendDist);
            app.Lifetime.ApplicationStopped.Register(files.Dispose);
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
            app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });
        }
        app.MapReverseProxy();
        return app;
    }

    private static async Task ReactFlow(IBrowser browser, string origin, ConcurrentQueue<ProxyObservation> observations)
    {
        Console.WriteLine("Starting real built React SPA flow.");
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        context.SetDefaultTimeout(15000);
        var page = await context.NewPageAsync();
        var email = "react-" + Guid.NewGuid().ToString("N") + "@example.test";
        await page.GotoAsync(origin + "/register");
        await page.GetByLabel("Full name", new() { Exact = true }).FillAsync("React TLS Fixture");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account", Exact = true }).ClickAsync();
        await page.GetByText("Your account was created.").WaitForAsync();
        Check(true, "actual React registration form creates a Customer through HTTPS and PostgreSQL");
        Console.WriteLine("React: opening the sign-in route.");
        await page.GetByRole(AriaRole.Link, new() { Name = "Sign in to continue.", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Welcome back", Exact = true }).WaitForAsync();
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        Console.WriteLine("React: submitting the sign-in form.");
        var loginResponse = page.WaitForResponseAsync(response => response.Url == origin + "/api/v1/auth/login" && response.Request.Method == "POST");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        Console.WriteLine($"React login HTTP status: {(await loginResponse).Status}");
        await page.GetByRole(AriaRole.Heading, new() { Name = "My profile", Exact = true }).WaitForAsync();
        await page.GetByLabel("Full name", new() { Exact = true }).WaitForAsync();
        Check(await page.GetByLabel("Full name", new() { Exact = true }).InputValueAsync() == "React TLS Fixture"
            && await page.GetByText(email).IsVisibleAsync(), "actual React login renders the authenticated Customer profile");
        await page.GetByLabel("Full name", new() { Exact = true }).FillAsync("React TLS Updated");
        await page.GetByLabel("Phone number", new() { Exact = true }).FillAsync("0771112233");
        await page.GetByLabel("Address", new() { Exact = true }).FillAsync("Fictional React fixture");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save profile", Exact = true }).ClickAsync();
        await page.GetByText("Your profile was saved.").WaitForAsync();
        Check(true, "actual React profile form saves its allowed fields with cookie/CSRF protection");
        var priorCookie = (await context.CookiesAsync()).Single(cookie => cookie.Name == RefreshCookieName).Value;
        await page.ReloadAsync();
        await page.GetByLabel("Full name", new() { Exact = true }).WaitForAsync();
        var restoredCookie = (await context.CookiesAsync()).Single(cookie => cookie.Name == RefreshCookieName).Value;
        Check(await page.GetByLabel("Full name", new() { Exact = true }).InputValueAsync() == "React TLS Updated"
            && restoredCookie != priorCookie, "actual React reload restores the session through rotating refresh and persisted profile");
        await page.GotoAsync(origin + "/admin/users");
        await page.GetByText("You do not have permission to view user administration.").WaitForAsync();
        Check(!observations.Any(item => item.Path == "/api/v1/users"),
            "actual Customer SPA navigation cannot load Admin user data");
        await page.GetByRole(AriaRole.Link, new() { Name = "My profile", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).WaitForAsync();
        var currentCookie = (await context.CookiesAsync()).Single(cookie => cookie.Name == RefreshCookieName).Value;
        Check(await page.EvaluateAsync<bool>("""
            secrets => !Object.entries(localStorage).concat(Object.entries(sessionStorage)).some(([key, value]) =>
              /token|password/i.test(key) || secrets.some(secret => value.includes(secret))
              || /eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\./.test(value))
            """, new[] { Password, priorCookie, restoredCookie, currentCookie }),
            "actual SPA keeps access/refresh credentials and passwords out of persistent browser storage");
        var logout = page.WaitForResponseAsync(response => response.Url == origin + "/api/v1/auth/logout" && response.Request.Method == "POST");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Welcome back", Exact = true }).WaitForAsync();
        Check((await logout).Status == 204 && (await context.CookiesAsync()).All(cookie => cookie.Name != RefreshCookieName),
            "actual React logout reaches Identity, clears the refresh cookie and returns to sign-in");
    }

    private static async Task FlutterFlow(string project, string origin, X509Certificate2 certificate, string owned)
    {
        var publicCertificate = Path.Combine(owned, "public-tls.crt");
        await File.WriteAllTextAsync(publicCertificate, certificate.ExportCertificatePem());
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "flutter")
        {
            WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("flutter");
        }
        foreach (var argument in new[] { "test", "--no-pub", "integration/identity_api_smoke.dart",
                     "--dart-define=IDENTITY_TEST_ORIGIN=" + origin, "--dart-define=IDENTITY_TEST_CERT=" + publicCertificate })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the standalone Flutter smoke test.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("The standalone Flutter test exceeded its owned process deadline.");
        }
        var completed = await output;
        _ = await error;
        // Do not echo arbitrary test failure details: they can include credentials.
        Check(process.ExitCode == 0 && completed.Contains("All tests passed!", StringComparison.Ordinal),
            "standalone Flutter native API/controller smoke passes against real HTTPS Identity using only the temporary public certificate");
    }

    private static async Task ReactAdminFlow(IBrowser browser, string origin, IdentityPostgresFixture database, string owned)
    {
        Console.WriteLine("Starting real built React Admin SPA flow.");
        var email = "admin-" + Guid.NewGuid().ToString("N") + "@example.test";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = database.ConnectionString,
            ["Identity:DevelopmentAdmin:Email"] = email,
            ["Identity:DevelopmentAdmin:FullName"] = "Fixture Admin",
            ["Identity:DevelopmentAdmin:Password"] = password
        }).Build();
        await DevelopmentAdminProvisioner.ProvisionAsync(configuration, new FixtureDevelopmentEnvironment(owned));
        // Force a second audit page with real registration-handler records. These
        // owned Customer fixtures never receive Admin/Courier privileges.
        var registration = new PostgresUserRegistrationStore(database.DataSource);
        var hashing = new Argon2PasswordHasher();
        for (var index = 0; index < 15; index++)
        {
            var fixtureEmail = "paging-" + Guid.NewGuid().ToString("N") + "@example.test";
            var fixtureHash = await hashing.HashAsync(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)), default);
            await registration.RegisterAsync(fixtureEmail, fixtureEmail.ToUpperInvariant(), "Owned paging fixture", fixtureHash, default);
        }
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        context.SetDefaultTimeout(15000);
        var page = await context.NewPageAsync();
        await page.GotoAsync(origin + "/login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "My profile", Exact = true }).WaitForAsync();
        var listed = page.WaitForResponseAsync(response => response.Url.StartsWith(origin + "/api/v1/users?", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Link, new() { Name = "User administration", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "User administration", Exact = true }).WaitForAsync();
        Check((await listed).Status == 200, "actual React Admin loads server-authorized paginated users");
        var create = page.Locator("section").Filter(new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Create user account", Exact = true }) });
        var createdEmail = "created-" + Guid.NewGuid().ToString("N") + "@example.test";
        var createdPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        await create.GetByLabel("Email", new() { Exact = true }).FillAsync(createdEmail);
        await create.GetByLabel("Full name", new() { Exact = true }).FillAsync("Admin Created Fixture");
        await create.GetByLabel("Initial password", new() { Exact = true }).FillAsync(createdPassword);
        await create.GetByRole(AriaRole.Button, new() { Name = "Review new account", Exact = true }).ClickAsync();
        var created = page.WaitForResponseAsync(response => response.Url == origin + "/api/v1/users" && response.Request.Method == "POST");
        await create.GetByRole(AriaRole.Button, new() { Name = "Confirm create account", Exact = true }).ClickAsync();
        var creation = await created;
        var profile = (await creation.JsonAsync())!.Value;
        var id = profile.GetProperty("id").GetGuid();
        Check(creation.Status == 201 && profile.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).SequenceEqual(["Customer"])
            && !profile.TryGetProperty("password", out _) && !profile.TryGetProperty("passwordHash", out _),
            "actual React Admin creates a Customer-only account with no password in the response");
        var editor = page.Locator("section").Filter(new() { Has = page.GetByText("Account ID: " + id, new() { Exact = true }) });
        await editor.GetByLabel("Full name", new() { Exact = true }).FillAsync("Admin Updated Fixture");
        await editor.GetByRole(AriaRole.Button, new() { Name = "Review account change", Exact = true }).ClickAsync();
        var updated = page.WaitForResponseAsync(response => response.Url == origin + "/api/v1/users/" + id && response.Request.Method == "PUT");
        await editor.GetByRole(AriaRole.Button, new() { Name = "Confirm change", Exact = true }).ClickAsync();
        Check((await updated).Status == 200 && await StoredName(database, id) == "Admin Updated Fixture",
            "actual React Admin confirmed update persists allowed account fields");
        await editor.GetByLabel("Courier", new() { Exact = true }).CheckAsync();
        await editor.GetByRole(AriaRole.Button, new() { Name = "Review role changes", Exact = true }).ClickAsync();
        var changedRoles = page.WaitForResponseAsync(response => response.Url == origin + "/api/v1/users/" + id + "/roles" && response.Request.Method == "PUT");
        await editor.GetByRole(AriaRole.Button, new() { Name = "Confirm change", Exact = true }).ClickAsync();
        var roleProfile = (await (await changedRoles).JsonAsync())!.Value;
        Check(roleProfile.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).SequenceEqual(["Courier", "Customer"]),
            "actual React Admin confirmed role replacement returns server-assigned Courier and Customer roles");
        await editor.GetByRole(AriaRole.Button, new() { Name = "Deactivate account", Exact = true }).ClickAsync();
        var deactivated = page.WaitForResponseAsync(response => response.Url == origin + "/api/v1/users/" + id && response.Request.Method == "DELETE");
        await editor.GetByRole(AriaRole.Button, new() { Name = "Confirm change", Exact = true }).ClickAsync();
        Check((await deactivated).Status == 204 && !await StoredActive(database, id),
            "actual React Admin confirmed deactivation preserves the account and disables it");
        await page.GetByRole(AriaRole.Button, new() { Name = "View audit log", Exact = true }).ClickAsync();
        var audit = page.Locator("section").Filter(new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Identity audit log", Exact = true }) });
        await audit.GetByRole(AriaRole.Table).WaitForAsync();
        var auditNext = audit.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true });
        var movedAuditPage = false;
        if (!await audit.GetByText("UserDeactivated", new() { Exact = true }).IsVisibleAsync() && await auditNext.IsEnabledAsync())
        {
            await auditNext.ClickAsync();
            await audit.GetByText("UserDeactivated", new() { Exact = true }).WaitForAsync();
            movedAuditPage = true;
        }
        Check(movedAuditPage && await audit.GetByText("UserDeactivated", new() { Exact = true }).IsVisibleAsync()
            && await audit.GetByText("RolesChanged", new() { Exact = true }).IsVisibleAsync(),
            "actual React Admin audit paging displays the committed role and deactivation actions");
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit " + email, Exact = true }).ClickAsync();
        var ownEditor = page.Locator("section").Filter(new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Edit Fixture Admin", Exact = true }) });
        await ownEditor.GetByLabel("Admin", new() { Exact = true }).UncheckAsync();
        await ownEditor.GetByRole(AriaRole.Button, new() { Name = "Review role changes", Exact = true }).ClickAsync();
        var lastAdmin = page.WaitForResponseAsync(response => response.Url.EndsWith("/roles", StringComparison.Ordinal) && response.Request.Method == "PUT");
        await ownEditor.GetByRole(AriaRole.Button, new() { Name = "Confirm change", Exact = true }).ClickAsync();
        await ownEditor.GetByText("At least one active Admin must remain.", new() { Exact = true }).WaitForAsync();
        Check((await lastAdmin).Status == 409 && await ActiveAdministratorCount(database) == 1,
            "actual React Admin last-active-Admin removal is rejected with no privileged state loss");
        await page.GetByRole(AriaRole.Link, new() { Name = "My profile", Exact = true }).ClickAsync();
        var logout = page.WaitForResponseAsync(response => response.Url == origin + "/api/v1/auth/logout" && response.Request.Method == "POST");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Welcome back", Exact = true }).WaitForAsync();
        Check((await logout).Status == 204 && (await context.CookiesAsync()).All(cookie => cookie.Name != RefreshCookieName),
            "actual React Admin logout reaches Identity and clears its refresh cookie");
    }

    private static async Task<string> StoredName(IdentityPostgresFixture database, Guid id)
    {
        await using var query = database.DataSource.CreateCommand("SELECT full_name FROM public.users WHERE id=@id");
        query.Parameters.AddWithValue("id", id);
        return (string)(await query.ExecuteScalarAsync())!;
    }

    private static async Task<bool> StoredActive(IdentityPostgresFixture database, Guid id)
    {
        await using var query = database.DataSource.CreateCommand("SELECT is_active FROM public.users WHERE id=@id");
        query.Parameters.AddWithValue("id", id);
        return (bool)(await query.ExecuteScalarAsync())!;
    }

    private static async Task<long> ActiveAdministratorCount(IdentityPostgresFixture database)
    {
        await using var query = database.DataSource.CreateCommand("SELECT COUNT(*) FROM public.users u JOIN public.user_roles r ON u.id=r.user_id WHERE u.is_active AND r.role_name='Admin'");
        return (long)(await query.ExecuteScalarAsync())!;
    }

    private sealed class FixtureDevelopmentEnvironment(string directory) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "OwnedIdentityBrowserFixture";
        public string ContentRootPath { get; set; } = directory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static WebApplication CreateHttpsPage(X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.MapGet("/", () => Results.Content("<!doctype html><title>Different-origin fixture</title>", "text/html"));
        return app;
    }

    private sealed record ProxyObservation(string Path, string Origin, int Status, bool HasRefreshCookie);

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // SChannel needs a named imported private key. DefaultKeySet deletes its
        // temporary backing key when this certificate is disposed; never persist
        // it or add the certificate to a system/browser trust store.
        var pfx = generated.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void EnsureOwnedDirectory(string directory, string parent)
    {
        var absolute = Path.GetFullPath(directory);
        var name = Path.GetFileName(absolute);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(absolute), Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)), comparison)
            || !name.StartsWith("swiftdrop-identity-browser-", StringComparison.Ordinal)
            || !Guid.TryParseExact(name["swiftdrop-identity-browser-".Length..], "N", out _))
            throw new InvalidOperationException("Refusing cleanup outside the owned browser fixture directory.");
    }

    private sealed class IdentityKestrelFactory(IdentityPostgresFixture database, string owned, string signingPath, string origin)
        : WebApplicationFactory<SwiftDrop.Identity.Api.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(IdentitySourceDirectory());
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = database.ConnectionString,
                ["Identity:Tokens:Issuer"] = origin,
                ["Identity:Tokens:Audience"] = "swiftdrop-browser-harness",
                ["Identity:Tokens:PrivateKeyPath"] = signingPath,
                ["Identity:Tokens:KeyId"] = "browser-harness",
                ["Identity:Browser:AllowedOrigins:0"] = origin,
                ["Identity:Proxy:KnownProxies:0"] = ProxyAddress.ToString(),
                ["Identity:Proxy:ForwardLimit"] = "1",
                ["Logging:EventLog:LogLevel:Default"] = "None"
            }));
            builder.ConfigureServices(services => services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(owned, "data-protection"))));
        }

        private static string IdentitySourceDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SwiftDrop.sln"))) directory = directory.Parent;
            return directory is null ? throw new InvalidOperationException("Run the harness from a backend checkout.")
                : Path.Combine(directory.FullName, "src", "Services", "Identity", "SwiftDrop.Identity.Api");
        }
    }

    private const string ClientScript = """
        (() => {
          let accessToken = null, csrfToken = null;
          window.identityHarness = {
            async call(options) {
              const headers = { ...(options.extraHeaders ?? {}) };
              if (options.body !== null) headers['Content-Type'] = 'application/json';
              if (options.csrf && options.method !== 'GET') headers['X-CSRF-Token'] = csrfToken;
              if (options.bearer) headers.Authorization = 'Bearer ' + accessToken;
              const response = await fetch(options.path, {
                method: options.method, credentials: 'include', cache: 'no-store', headers,
                signal: AbortSignal.timeout(15000),
                ...(options.body !== null ? { body: JSON.stringify(options.body) } : {})
              });
              let value = {};
              if (response.status !== 204) {
                try { value = await response.json(); }
                catch { return { status: response.status, invalidJson: true }; }
              }
              if (options.saveBootstrap && response.ok) csrfToken = value.csrfToken;
              if (options.saveAccess && response.ok) accessToken = value.accessToken;
              return { status: response.status, userId: value.id ?? value.user?.id ?? null,
                fullName: value.fullName ?? value.user?.fullName ?? null,
                hasRefreshToken: Object.hasOwn(value, 'refreshToken') };
            }
          };
        })()
        """;
}
