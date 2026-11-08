using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwiftDrop.Identity.Api.Application;

namespace SwiftDrop.Identity.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public async Task RegistrationCreatesCustomerProfileAndStoresOnlyHash()
    {
        var store = new TestRegistrationStore();
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email = " Customer@Example.com ", password = "Correct Horse Battery 7!", fullName = "  Customer One  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<RegisteredProfile>();
        Assert.NotNull(profile);
        Assert.Equal("Customer@Example.com", profile.Email);
        Assert.Equal("Customer One", profile.FullName);
        Assert.Equal(["Customer"], profile.Roles);
        Assert.DoesNotContain("Correct Horse Battery 7!", store.PasswordHash);
        Assert.StartsWith("argon2id$v=19$m=65536,t=3,p=1$", store.PasswordHash);
        Assert.Equal("CUSTOMER@EXAMPLE.COM", store.NormalizedEmail);
    }

    [Fact]
    public async Task DuplicateRegistrationReturnsGenericConflict()
    {
        var store = new TestRegistrationStore { IsDuplicate = true };
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email = "customer@example.com", password = "Correct Horse Battery 7!", fullName = "Customer One" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("registration_conflict", body);
        Assert.DoesNotContain("customer@example.com", body);
    }

    [Theory]
    [InlineData("invalid", "Correct Horse Battery 7!", "Customer One")]
    [InlineData("customer@example.com", "short", "Customer One")]
    [InlineData("customer@example.com", "Correct Horse Battery 7!", " ")]
    public async Task InvalidRegistrationReturnsValidationProblem(
        string email,
        string password,
        string fullName)
    {
        var store = new TestRegistrationStore();
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email, password, fullName });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(store.WasCalled);
    }

    [Fact]
    public async Task RegistrationLimitIgnoresUntrustedForwardedAddresses()
    {
        var store = new TestRegistrationStore();
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await PostInvalidRegistrationAsync(
                client,
                $"198.51.100.{attempt}, 203.0.113.7");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var limitedResponse = await PostInvalidRegistrationAsync(
            client,
            "198.51.100.99, 203.0.113.7");
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);

        using var otherClientResponse = await PostInvalidRegistrationAsync(
            client,
            "198.51.100.99, 203.0.113.8");
        Assert.Equal(HttpStatusCode.TooManyRequests, otherClientResponse.StatusCode);
    }

    [Fact]
    public async Task RegistrationWithoutNeonReturnsServiceUnavailableButHealthRemainsAvailable()
    {
        using var factory = CreateFactory(new TestRegistrationStore(), databaseConfigured: false);
        using var client = factory.CreateClient();

        using var healthResponse = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);

        using var readyResponse = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyResponse.StatusCode);

        using var registrationResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email = "customer@example.com", password = "Correct Horse Battery 7!", fullName = "Customer One" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, registrationResponse.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostInvalidRegistrationAsync(
        HttpClient client,
        string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new
            {
                email = "customer@example.com",
                password = "short",
                fullName = "Customer One"
            })
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return await client.SendAsync(request);
    }

    private static WebApplicationFactory<SwiftDrop.Identity.Api.Program> CreateFactory(
        IUserRegistrationStore store,
        bool databaseConfigured = true) =>
        new WebApplicationFactory<SwiftDrop.Identity.Api.Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                if (databaseConfigured)
                {
                    builder.ConfigureAppConfiguration((_, configuration) =>
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:Database"] =
                                "Host=localhost;Database=identity_test;Username=test;Password=test"
                        }));
                }

                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IUserRegistrationStore>();
                    services.AddSingleton(store);
                });
            });

    private sealed class TestRegistrationStore : IUserRegistrationStore
    {
        public bool IsDuplicate { get; init; }
        public bool WasCalled { get; private set; }
        public string? NormalizedEmail { get; private set; }
        public string? PasswordHash { get; private set; }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<RegisteredProfile?> RegisterAsync(
            string email,
            string normalizedEmail,
            string fullName,
            string passwordHash,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            NormalizedEmail = normalizedEmail;
            PasswordHash = passwordHash;
            return Task.FromResult<RegisteredProfile?>(IsDuplicate
                ? null
                : new RegisteredProfile(Guid.NewGuid(), email, fullName, null, null, ["Customer"], true));
        }
    }
}
