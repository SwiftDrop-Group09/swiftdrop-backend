using System.Net;
using System.Net.Http.Json;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

using SwiftDrop.Identity.Api.Infrastructure.Authentication;
using SwiftDrop.Identity.Api.Presentation;

namespace SwiftDrop.Identity.Tests;

public sealed class IdentityProxyTests
{
    private const string PublicOrigin = "https://app.example.test";
    private const string GatewayIp = "10.44.0.2";

    [Fact]
    public async Task NoConfiguredProxiesIgnoresForwardedHeadersIncludingLoopbackPeer()
    {
        using var server = CreateServer(Configuration(), "127.0.0.1");
        using var client = server.GetTestClient();
        using var request = ForwardedRequest("203.0.113.7");
        using var response = await client.SendAsync(request);
        var state = await response.Content.ReadFromJsonAsync<RequestState>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new RequestState("127.0.0.1", "http", "identity.internal:8080"), state);
    }

    [Fact]
    public async Task UntrustedPeerCannotRewriteClientIpHostOrScheme()
    {
        using var server = CreateServer(Configuration(GatewayIp), "10.44.0.3");
        using var client = server.GetTestClient();
        using var request = ForwardedRequest("203.0.113.7");
        using var response = await client.SendAsync(request);
        var state = await response.Content.ReadFromJsonAsync<RequestState>();

        Assert.Equal(new RequestState("10.44.0.3", "http", "identity.internal:8080"), state);
    }

    [Fact]
    public async Task TrustedGatewayRestoresHttpsPublicHostAndClientIp()
    {
        using var server = CreateServer(Configuration(GatewayIp), GatewayIp);
        using var client = server.GetTestClient();
        using var request = ForwardedRequest("203.0.113.7");
        using var response = await client.SendAsync(request);
        var state = await response.Content.ReadFromJsonAsync<RequestState>();

        Assert.Equal(new RequestState("203.0.113.7", "https", "app.example.test"), state);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("5")]
    public async Task RightmostHopStopsBeforeClientSuppliedSpoofedPrefix(string forwardLimit)
    {
        var configuration = Configuration(GatewayIp);
        configuration["Identity:Proxy:ForwardLimit"] = forwardLimit;
        using var server = CreateServer(configuration, GatewayIp);
        using var client = server.GetTestClient();
        using var request = ForwardedRequest("198.51.100.66, 203.0.113.7", "evil.example.test, app.example.test", "http, https");
        using var response = await client.SendAsync(request);
        var state = await response.Content.ReadFromJsonAsync<RequestState>();

        Assert.Equal(new RequestState("203.0.113.7", "https", "app.example.test"), state);
    }

    [Fact]
    public async Task ExplicitTrustedProxyChainUsesConfiguredBoundedHopCount()
    {
        var configuration = Configuration(GatewayIp, "10.44.0.1");
        configuration["Identity:Proxy:ForwardLimit"] = "2";
        using var server = CreateServer(configuration, GatewayIp);
        using var client = server.GetTestClient();
        using var request = ForwardedRequest("203.0.113.7, 10.44.0.1", "app.example.test, app.example.test", "https, https");
        using var response = await client.SendAsync(request);
        var state = await response.Content.ReadFromJsonAsync<RequestState>();

        Assert.Equal(new RequestState("203.0.113.7", "https", "app.example.test"), state);
    }

    [Fact]
    public async Task TrustedGatewayCannotSupplyAnUnlistedForwardedHost()
    {
        using var server = CreateServer(Configuration(GatewayIp), GatewayIp);
        using var client = server.GetTestClient();
        using var request = ForwardedRequest("203.0.113.7", "evil.example.test");
        using var response = await client.SendAsync(request);
        var state = await response.Content.ReadFromJsonAsync<RequestState>();

        Assert.NotNull(state);
        Assert.Equal("identity.internal:8080", state.Host);
    }

    [Fact]
    public async Task ExplicitIpv6ProxyAndLoopbackOriginRestoreAddressAndPort()
    {
        var configuration = Configuration("::1");
        configuration["Identity:Browser:AllowedOrigins:0"] = "https://[::1]:5173";
        using var server = CreateServer(configuration, "::1");
        using var client = server.GetTestClient();
        using var request = ForwardedRequest("2001:db8::42", "[::1]:5173");
        using var response = await client.SendAsync(request);
        var state = await response.Content.ReadFromJsonAsync<RequestState>();

        Assert.Equal(new RequestState("2001:db8::42", "https", "[::1]:5173"), state);
    }

    [Fact]
    public async Task SameOriginBootstrapWithoutOriginWorksThroughTrustedHttpsGateway()
    {
        using var server = CreateServer(Configuration(GatewayIp), GatewayIp, bootstrap: true);
        using var client = server.GetTestClient();
        using var request = ForwardedRequest("203.0.113.7");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("csrfToken", body, StringComparison.Ordinal);
        Assert.Contains("googleNonce", body, StringComparison.Ordinal);
        Assert.All(response.Headers.GetValues("Set-Cookie"), cookie =>
        {
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task SpoofedProxyHeadersCannotBootstrapCsrfFromUntrustedPeer()
    {
        using var server = CreateServer(Configuration(GatewayIp), "10.44.0.3", bootstrap: true);
        using var client = server.GetTestClient();
        using var request = ForwardedRequest("203.0.113.7");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task AllowedForwardedHostnameDoesNotBypassExactBootstrapOriginPort()
    {
        using var server = CreateServer(Configuration(GatewayIp), GatewayIp, bootstrap: true);
        using var client = server.GetTestClient();
        using var request = ForwardedRequest("203.0.113.7", "app.example.test:8443");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RateLimitPartitionsUseTrustedClientAddressesAndIgnoreUntrustedSpoofs(bool trusted)
    {
        using var server = CreateServer(Configuration(GatewayIp), trusted ? GatewayIp : "10.44.0.3", rateLimit: true);
        using var client = server.GetTestClient();
        using var first = ForwardedRequest("203.0.113.7");
        using var firstResponse = await client.SendAsync(first);
        using var sameClient = ForwardedRequest("203.0.113.7");
        using var sameResponse = await client.SendAsync(sameClient);
        using var differentClient = ForwardedRequest("203.0.113.8");
        using var differentResponse = await client.SendAsync(differentClient);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, sameResponse.StatusCode);
        Assert.Equal(trusted ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, differentResponse.StatusCode);
    }

    [Theory]
    [InlineData("gateway.internal", "1")]
    [InlineData("10.44.0.0/24", "1")]
    [InlineData("*", "1")]
    [InlineData("10.44.0.2", "0")]
    [InlineData("10.44.0.2", "6")]
    [InlineData("10.44.0.2", "unlimited")]
    public void InvalidProxyTrustConfigurationFailsBeforeServingRequests(string proxy, string limit)
    {
        var configuration = Configuration(proxy);
        configuration["Identity:Proxy:ForwardLimit"] = limit;
        var exception = Assert.Throws<InvalidOperationException>(() => CreateServer(configuration, GatewayIp));

        Assert.Contains("Identity proxy trust configuration is invalid", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(proxy, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://*.example.test")]
    [InlineData("https://app.example.test/path")]
    public void ConfiguredProxyRequiresExplicitValidForwardedHostAllowlist(string? origin)
    {
        var configuration = Configuration(GatewayIp);
        configuration["Identity:Browser:AllowedOrigins:0"] = origin;

        Assert.Throws<InvalidOperationException>(() => CreateServer(configuration, GatewayIp));
    }

    private static IConfigurationRoot Configuration(params string[] proxies)
    {
        var values = new Dictionary<string, string?>
        {
            ["Identity:Browser:AllowedOrigins:0"] = PublicOrigin
        };
        for (var index = 0; index < proxies.Length; index++)
        {
            values[$"Identity:Proxy:KnownProxies:{index}"] = proxies[index];
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static HttpRequestMessage ForwardedRequest(string clientIp,
        string host = "app.example.test", string scheme = "https")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "http://identity.internal:8080/api/v1/auth/csrf");
        request.Headers.Add("X-Forwarded-For", clientIp);
        request.Headers.Add("X-Forwarded-Host", host);
        request.Headers.Add("X-Forwarded-Proto", scheme);
        return request;
    }

    private static IHost CreateServer(IConfiguration configuration, string remoteIp,
        bool bootstrap = false, bool rateLimit = false)
    {
        return new HostBuilder().UseEnvironment("Testing")
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHost(builder => builder.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddSingleton(configuration);
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddAntiforgery(options =>
                {
                    options.HeaderName = "X-CSRF-Token";
                    options.Cookie.Name = "__Host-swiftdrop.csrf";
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Strict;
                    options.Cookie.Path = "/";
                });
                services.AddSingleton(TimeProvider.System);
                services.AddScoped<BrowserAuthentication>();
                services.AddRateLimiter(options =>
                {
                    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress!.ToString(),
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = 1,
                                Window = TimeSpan.FromMinutes(1),
                                QueueLimit = 0
                            }));
                });
            })
            .Configure(app =>
            {
                app.Use((context, next) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
                    context.Connection.RemotePort = 34120;
                    return next(context);
                });
                app.UseIdentityProxyForwarding(configuration);
                if (rateLimit)
                {
                    app.UseRateLimiter();
                }

                app.Run(async context =>
                {
                    if (bootstrap)
                    {
                        await context.RequestServices.GetRequiredService<BrowserAuthentication>()
                            .Bootstrap(context).ExecuteAsync(context);
                    }
                    else
                    {
                        await context.Response.WriteAsJsonAsync(new RequestState(
                            context.Connection.RemoteIpAddress!.ToString(), context.Request.Scheme,
                            context.Request.Host.ToString()));
                    }
                });
            }))
            .Start();
    }

    private sealed record RequestState(string Ip, string Scheme, string Host);
}
