using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SwiftDrop.Delivery.Tests;

public sealed class FoundationTests : IDisposable
{
    private readonly WebApplicationFactory<SwiftDrop.Delivery.Api.Program> _factory =
        new WebApplicationFactory<SwiftDrop.Delivery.Api.Program>()
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
    [InlineData("/api/v1/parcels")]
    [InlineData("/graphql")]
    [InlineData("/api/v1/parcels/example/events")]
    [InlineData("/internal/v1/notification-context")]
    [InlineData("/swiftdrop.delivery.v1.DeliveryService/CreateBooking")]
    public async Task UnimplementedRoutesReturnNotFound(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/quotes")]
    [InlineData("/api/v1/parcels")]
    public async Task PersistenceFoundationDoesNotExposeAnonymousFeatureWrites(string path)
    {
        using var client = _factory.CreateClient();
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
