using System.Globalization;
using System.Net;

using Microsoft.AspNetCore.HttpOverrides;

using SwiftDrop.Identity.Api.Presentation;

namespace SwiftDrop.Identity.Api.Infrastructure.Authentication;

public static class IdentityProxyExtensions
{
    public static IApplicationBuilder UseIdentityProxyForwarding(
        this IApplicationBuilder app,
        IConfiguration configuration)
    {
        var configuredLimit = configuration["Identity:Proxy:ForwardLimit"];
        if (configuredLimit is not null
            && (!int.TryParse(configuredLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLimit)
                || parsedLimit is < 1 or > 5))
        {
            throw Unavailable();
        }

        var limit = configuredLimit is null ? 1 : int.Parse(configuredLimit, CultureInfo.InvariantCulture);
        var proxySection = configuration.GetSection("Identity:Proxy:KnownProxies");
        if (proxySection.Value is not null)
        {
            throw Unavailable();
        }

        var proxyValues = proxySection.GetChildren().Select(child => child.Value).ToArray();
        if (proxyValues.Length == 0)
        {
            // The framework's built-in loopback trust is also disabled here.
            // Clearing both trust lists and still installing the middleware
            // would instead trust every peer, so do not install it at all.
            return app;
        }

        var proxies = new List<IPAddress>();
        foreach (var value in proxyValues)
        {
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim()
                || !IPAddress.TryParse(value, out var address))
            {
                throw Unavailable();
            }

            proxies.Add(address);
        }

        var origins = configuration.GetSection("Identity:Browser:AllowedOrigins").Get<string[]>() ?? [];
        if (origins.Length == 0 || origins.Any(origin => !BrowserAuthentication.ValidOrigin(origin)))
        {
            // An empty forwarded-host allowlist means allow every host in the
            // framework. Require explicit browser hosts before trusting it.
            throw Unavailable();
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto,
            ForwardLimit = limit
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in proxies.Distinct())
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (var host in origins.Select(origin => new Uri(origin).Host).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            options.AllowedHosts.Add(host);
        }

        return app.UseForwardedHeaders(options);
    }

    private static InvalidOperationException Unavailable() =>
        new("Identity proxy trust configuration is invalid; configure explicit proxy IPs, browser origins and a forward limit from 1 to 5.");
}
