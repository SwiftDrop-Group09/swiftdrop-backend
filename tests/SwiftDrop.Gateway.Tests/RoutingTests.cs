using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yarp.ReverseProxy.Forwarder;

namespace SwiftDrop.Gateway.Tests;

public sealed class RoutingTests : IDisposable
{
    private readonly WebApplicationFactory<SwiftDrop.Gateway.Program> _factory =
        new WebApplicationFactory<SwiftDrop.Gateway.Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IForwarderHttpClientFactory>();
                    services.AddSingleton<IForwarderHttpClientFactory, ProbeClientFactory>();
                });
            });

    // Snapshot of docs commit 233f707; tests exercise the real YARP matching pipeline.
    public static IEnumerable<object[]> DocumentedRoutes()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "routes.json"));
        var routes = JsonSerializer.Deserialize<List<RouteFixture>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Route fixture is missing.");

        foreach (var route in routes)
        {
            yield return [route.Method, Regex.Replace(route.Path, @"\{[^}]+\}", "sample"), route.Owner];
        }
    }

    [Theory]
    [MemberData(nameof(DocumentedRoutes))]
    public async Task DocumentedRouteReachesOnlyItsOwner(string method, string path, string owner)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path + "?page=2");
        using var response = await client.SendAsync(request);

        if (path.StartsWith("/internal/", StringComparison.Ordinal))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            return;
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            $"{method} http://{owner.ToLowerInvariant()}-service:8080{path}?page=2",
            await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task GraphQlRoutesToDelivery(string method)
    {
        using var client = _factory.CreateClient();
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/graphql"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal($"{method} http://delivery-service:8080/graphql", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/internal/anything")]
    [InlineData("/swiftdrop.delivery.v1.DeliveryService/CreateBooking")]
    [InlineData("/api/v1/notifications")]
    [InlineData("/api/v1/merchant/unknown")]
    [InlineData("/api/v1/reports/unknown")]
    [InlineData("/hubs/tracking")]
    [InlineData("/weatherforecast")]
    [InlineData("/ready")]
    public async Task UnownedPathsAreNotForwarded(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GatewayLivenessDoesNotDependOnDestinations()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => _factory.Dispose();

    public sealed record RouteFixture(string Method, string Path, string Owner);

    private sealed class ProbeClientFactory : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) =>
            new(new ProbeHandler());
    }

    // Test-only transport: reports the selected destination; no business response is simulated.
    private sealed class ProbeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent($"{request.Method} {request.RequestUri}")
            });
    }
}
