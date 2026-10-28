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
    [InlineData("/graphql")]
    [InlineData("/api/v1/parcels/example/events")]
    [InlineData("/internal/v1/notification-context")]
    [InlineData("/swiftdrop.delivery.v1.DeliveryService/CreateBooking")]
    [InlineData("/swiftdrop.delivery.v1.DeliveryService/GetBookingByExternalOrder")]
    public async Task UnimplementedRoutesReturnNotFound(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/quotes", HttpStatusCode.Unauthorized)]
    [InlineData("/api/v1/parcels", HttpStatusCode.Unauthorized)]
    public async Task FeatureWritesRequireAuthenticationOrRemainUnimplemented(string path, HttpStatusCode expectedStatus)
    {
        using var client = _factory.CreateClient();
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content);

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    [Fact]
    public async Task ParcelCollectionGetIsNotImplemented()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/parcels");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/couriers/me/assignments")]
    [InlineData("/api/v1/parcels/11111111-1111-4111-8111-111111111111")]
    public async Task PrivateDeliveryReadsRequireVerifiedAuthentication(string path)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-User-Role", "Courier");
        client.DefaultRequestHeaders.Add("Authorization", "Bearer unverified-read-token");
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task CourierStatusWriteRequiresVerifiedAuthentication()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-User-Role", "Courier");
        client.DefaultRequestHeaders.Add("Authorization", "Bearer unverified-status-token");
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PutAsync("/api/v1/deliveries/11111111-1111-4111-8111-111111111111/status", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
    }

    [Theory]
    [InlineData("CreateBooking")]
    [InlineData("GetBookingByExternalOrder")]
    public async Task MerchantRpcPostsRemainUnimplementedDespiteCallerSuppliedCredentials(string method)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString("D"));
        client.DefaultRequestHeaders.Add("X-User-Role", "Admin");
        client.DefaultRequestHeaders.Add("X-API-Key", "fictional-merchant-key");
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/grpc+json");
        using var response = await client.PostAsync($"/swiftdrop.delivery.v1.DeliveryService/{method}", content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
