using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SwiftDrop.Merchant.Tests;

public sealed class FoundationTests : IDisposable
{
    private readonly WebApplicationFactory<SwiftDrop.Merchant.Api.Program> _factory =
        new WebApplicationFactory<SwiftDrop.Merchant.Api.Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));

    [Fact]
    public async Task LivenessDoesNotRequireExternalInfrastructure()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/weatherforecast")]
    [InlineData("/ready")]
    [InlineData("/api/v1/merchant/orders")]
    public async Task UnimplementedRoutesReturnNotFound(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/merchants")]
    [InlineData("/api/v1/merchants/me")]
    [InlineData("/api/v1/merchants/api-keys")]
    [InlineData("/api/v1/merchant/webhooks")]
    public async Task FeatureRoutesRequireAuthentication(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
