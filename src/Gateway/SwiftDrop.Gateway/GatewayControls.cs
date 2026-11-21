using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace SwiftDrop.Gateway;

internal static class GatewayControls
{
    private const string CorrelationHeader = "X-Correlation-ID";
    internal const long MaxRequestBodyBytes = 12 * 1024 * 1024;

    public static void AddGatewayRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRateLimiter(options =>
        {
            var limits = configuration.GetSection("Gateway:RateLimits");
            var authLimit = ReadLimit(limits, "AuthenticationPerMinute", 60);
            var trackingLimit = ReadLimit(limits, "PublicTrackingPerMinute", 120);
            var assistantLimit = ReadLimit(limits, "AssistantPerMinute", 30);
            var streamLimit = ReadLimit(limits, "TrackingStreamsPerClient", 8);

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                var response = context.HttpContext.Response;
                response.Headers.CacheControl = "no-store";
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    response.Headers.RetryAfter = Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(CultureInfo.InvariantCulture);
                }

                await response.WriteAsJsonAsync(new { code = "rate_limit_exceeded" }, cancellationToken);
            };
            options.AddPolicy("authentication", context => PerMinute(context, authLimit));
            options.AddPolicy("public-tracking", context => PerMinute(context, trackingLimit));
            options.AddPolicy("assistant", context => PerMinute(context, assistantLimit));
            options.AddPolicy("tracking-streams", context =>
                RateLimitPartition.GetConcurrencyLimiter(ClientAddress(context), _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = streamLimit,
                    QueueLimit = 0
                }));
        });
    }

    public static void UseGatewayForwarding(this WebApplication app)
    {
        var knownProxies = app.Configuration.GetSection("Gateway:Forwarding:KnownProxies");
        var entries = knownProxies.GetChildren().ToArray();
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var entry in entries)
        {
            if (!IPAddress.TryParse(entry.Value, out var address) ||
                address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                throw new InvalidOperationException("Gateway forwarding requires explicit proxy IP addresses.");
            }

            options.KnownProxies.Add(address);
        }

        if (knownProxies.Value is not null)
        {
            throw new InvalidOperationException("Gateway forwarding requires an array of proxy IP addresses.");
        }

        // Empty trust lists mean trust every peer to the framework; leave forwarding disabled in that case.
        if (entries.Length > 0)
        {
            app.UseForwardedHeaders(options);
        }
    }

    public static async Task CorrelateAndBoundRequest(HttpContext context, RequestDelegate next)
    {
        var supplied = context.Request.Headers[CorrelationHeader];
        var correlationId = supplied.Count == 1 && Guid.TryParseExact(supplied[0], "D", out var id) && id != Guid.Empty
            ? id.ToString("D")
            : Guid.NewGuid().ToString("D");
        context.TraceIdentifier = correlationId;
        context.Request.Headers[CorrelationHeader] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationHeader] = correlationId;
            return Task.CompletedTask;
        });

        if (context.Request.ContentLength > MaxRequestBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { code = "request_too_large" }, context.RequestAborted);
            return;
        }

        await next(context);
    }

    private static RateLimitPartition<string> PerMinute(HttpContext context, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(ClientAddress(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });

    private static string ClientAddress(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return (address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address)?.ToString() ?? "unknown";
    }

    private static int ReadLimit(IConfiguration section, string name, int fallback)
    {
        var limit = section.GetValue(name, fallback);
        return limit > 0
            ? limit
            : throw new InvalidOperationException($"Gateway rate limit {name} must be positive.");
    }
}
