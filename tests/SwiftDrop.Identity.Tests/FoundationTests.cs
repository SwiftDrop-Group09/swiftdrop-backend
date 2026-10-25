using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SwiftDrop.Identity.Tests;

public sealed class FoundationTests : IDisposable
{
    private readonly WebApplicationFactory<SwiftDrop.Identity.Api.Program> _factory =
        new WebApplicationFactory<SwiftDrop.Identity.Api.Program>()
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
    [InlineData("/api/v1/users")]
    public async Task UnimplementedRoutesReturnNotFound(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
