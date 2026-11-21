using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yarp.ReverseProxy.Forwarder;

namespace SwiftDrop.Gateway.Tests;

public sealed class GatewayControlsTests
{
    private const string CorrelationHeader = "X-Correlation-ID";
    private const string Peer = "192.0.2.5";

    [Theory]
    [InlineData("POST", "/api/v1/auth/login")]
    [InlineData("GET", "/api/v1/parcels/track/sample")]
    [InlineData("POST", "/api/v1/assistant/chat")]
    [InlineData("POST", "/api/v1/mcp")]
    public async Task RequestBudgetsRejectExcessTrafficWithSafeCorrelatedResponse(string method, string path)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var first = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        using var rejected = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.True(rejected.Headers.CacheControl?.NoStore);
        Assert.Equal("{\"code\":\"rate_limit_exceeded\"}", await rejected.Content.ReadAsStringAsync());
        Assert.True(Guid.TryParseExact(rejected.Headers.GetValues(CorrelationHeader).Single(), "D", out _));
    }

    [Theory]
    [InlineData(null, Peer, "http")]
    [InlineData("192.0.2.20", Peer, "http")]
    [InlineData(Peer, "198.51.100.7", "https")]
    public async Task ForwardingUsesOnlyTheConfiguredPeer(string? trustedPeer, string expectedClient, string expectedScheme)
    {
        using var factory = CreateFactory(trustedPeer);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login");
        request.Headers.Add("X-Forwarded-For", "203.0.113.20, 198.51.100.7");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "discard.example");
        request.Headers.Add("X-Original-For", "203.0.113.20");
        request.Headers.Add("Forwarded", "for=203.0.113.20");

        using var response = await client.SendAsync(request);
        var probe = await ReadProbe(response);
        Assert.Equal(expectedClient, probe.Client);
        Assert.Equal(expectedScheme, probe.Scheme);
        Assert.Equal("localhost", probe.Host);
        Assert.False(probe.ForwardingResidue);

        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login");
        second.Headers.Add("X-Forwarded-For", "198.51.100.8");
        using var secondResponse = await client.SendAsync(second);
        Assert.Equal(trustedPeer == Peer ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
    }

    [Theory]
    [InlineData("6cf68299-986b-46a1-9437-064aac16d0e6", true)]
    [InlineData("arbitrary-caller-text", false)]
    [InlineData("6cf68299-986b-46a1-9437-064aac16d0e6, 6cf68299-986b-46a1-9437-064aac16d0e6", false)]
    [InlineData(null, false)]
    public async Task CorrelationMatchesUpstreamAndClientResponse(string? supplied, bool preserve)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/parcels/summary");
        if (supplied is not null)
        {
            request.Headers.Add(CorrelationHeader, supplied);
        }

        using var response = await client.SendAsync(request);
        var probe = await ReadProbe(response);
        Assert.True(Guid.TryParseExact(probe.Correlation, "D", out var correlation));
        Assert.NotEqual(Guid.Empty, correlation);
        Assert.Equal(probe.Correlation, response.Headers.GetValues(CorrelationHeader).Single());
        Assert.Equal(preserve, supplied == probe.Correlation);
    }

    [Theory]
    [InlineData("/api/v1/parcels", 64)]
    [InlineData("/api/v1/merchant/orders", 128)]
    [InlineData("/api/v1/deliveries/sample/pod", 2 * 5 * 1024 * 1024 + 64 * 1024)]
    public async Task GatewayPreservesRequestBodyAndServiceCredentials(string path, int bytes)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(new byte[bytes]) };
        request.Headers.Add("Authorization", "Bearer placeholder");
        request.Headers.Add("X-API-Key", "placeholder");
        request.Headers.Add("Idempotency-Key", "gateway-test");

        using var response = await client.SendAsync(request);
        var probe = await ReadProbe(response);
        Assert.Equal(bytes, probe.Length);
        Assert.Equal("Bearer placeholder", probe.Authorization);
        Assert.Equal("placeholder", probe.ApiKey);
        Assert.Equal("gateway-test", probe.IdempotencyKey);
    }

    [Fact]
    public async Task OversizedDeclaredBodyReceivesStableGatewayError()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var content = new ByteArrayContent(new byte[12 * 1024 * 1024 + 1]);
        using var rejected = await client.PostAsync("/api/v1/assistant/chat", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        Assert.Equal("{\"code\":\"request_too_large\"}", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TrackingStreamKeepsItsPermitWhileSendingFramesAndReleasesItOnCompletion()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var cancellationToken = timeout.Token;
        var stream = new Pipe();
        using var factory = CreateFactory(stream: stream);
        using var client = factory.CreateClient();

        await stream.Writer.WriteAsync("data: first\n\n"u8.ToArray(), cancellationToken);
        using var first = await client.GetAsync("/api/v1/parcels/sample/events",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Assert.Equal("text/event-stream", first.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await first.Content.ReadAsStreamAsync(cancellationToken));
        Assert.Equal("data: first", await reader.ReadLineAsync(cancellationToken));
        Assert.Equal("", await reader.ReadLineAsync(cancellationToken));

        using var busy = await client.GetAsync("/api/v1/parcels/track/another/events", cancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        using var tracking = await client.GetAsync("/api/v1/parcels/track/sample", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, tracking.StatusCode);

        await stream.Writer.WriteAsync("data: second\n\n"u8.ToArray(), cancellationToken);
        await stream.Writer.CompleteAsync();
        Assert.Equal("data: second", await reader.ReadLineAsync(cancellationToken));
        Assert.Equal("", await reader.ReadLineAsync(cancellationToken));
        Assert.Null(await reader.ReadLineAsync(cancellationToken));
        using var after = await client.GetAsync("/api/v1/parcels/track/another/events", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    private static async Task<Probe> ReadProbe(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Probe>())!;
    }

    private static WebApplicationFactory<Program> CreateFactory(string? trustedPeer = null, Pipe? stream = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:RateLimits:AuthenticationPerMinute"] = "1",
                    ["Gateway:RateLimits:PublicTrackingPerMinute"] = "1",
                    ["Gateway:RateLimits:AssistantPerMinute"] = "1",
                    ["Gateway:RateLimits:TrackingStreamsPerClient"] = "1"
                });
                if (trustedPeer is not null)
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Gateway:Forwarding:KnownProxies:0"] = trustedPeer
                    });
                }
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, SetPeer>();
                services.RemoveAll<IForwarderHttpClientFactory>();
                services.AddSingleton<IForwarderHttpClientFactory>(new ProbeFactory(stream));
            });
        });

    private sealed class SetPeer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(Peer);
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class ProbeFactory(Pipe? stream) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new ProbeHandler(stream));
    }

    private sealed class ProbeHandler(Pipe? stream) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (stream is not null && request.RequestUri?.AbsolutePath == "/api/v1/parcels/sample/events")
            {
                return new(HttpStatusCode.OK) { Content = new StreamContent(stream.Reader.AsStream()) { Headers = { ContentType = new("text/event-stream") } } };
            }

            using var body = new MemoryStream();
            if (request.Content is not null)
            {
                await request.Content.CopyToAsync(body, cancellationToken);
            }

            string? Header(string name) => request.Headers.TryGetValues(name, out var values)
                ? string.Join(", ", values) : null;

            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new Probe(body.Length, Header(CorrelationHeader),
                    Header("X-Forwarded-For"), Header("X-Forwarded-Proto"), Header("X-Forwarded-Host"),
                    Header("Forwarded") is not null || Header("X-Original-For") is not null,
                    Header("Authorization"), Header("X-API-Key"), Header("Idempotency-Key")))
            };
        }
    }

    private sealed record Probe(long Length, string? Correlation, string? Client, string? Scheme, string? Host,
        bool ForwardingResidue, string? Authorization, string? ApiKey, string? IdempotencyKey);
}
