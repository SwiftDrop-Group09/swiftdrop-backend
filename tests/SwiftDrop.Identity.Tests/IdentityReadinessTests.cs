using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SwiftDrop.Identity.Tests;

public sealed class IdentityReadinessTests
{
    private const string FictionalPassword = "identity-readiness-fictional-password";

    [Theory]
    [InlineData("swiftdrop_identity")]
    [InlineData("swiftdrop_delivery")]
    public async Task UnreachableOrForeignDatabaseReturnsSafeUnavailableWhileHealthRemainsLive(string database)
    {
        using var factory = Factory(
            $"Host=127.0.0.1;Port=1;Database={database};Username=identity_readiness_test;Password={FictionalPassword};Timeout=1;Pooling=false");
        using var client = Client(factory);
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());

        using var readiness = await client.GetAsync("/ready");
        await AssertUnavailableAsync(readiness);
        using var registration = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "readiness-customer@example.test",
            password = "Correct Horse Battery 7!",
            fullName = "Readiness Customer"
        });
        await AssertUnavailableAsync(registration);
    }

    [Fact]
    public async Task MalformedDatabaseConfigurationReturnsSafeUnavailableWithoutBreakingLiveness()
    {
        using var factory = Factory("UnsupportedIdentityConnectionProperty=fictional-value");
        using var client = Client(factory);
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var readiness = await client.GetAsync("/ready");
        await AssertUnavailableAsync(readiness);
        using var registration = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "readiness-customer@example.test",
            password = "Correct Horse Battery 7!",
            fullName = "Readiness Customer"
        });
        await AssertUnavailableAsync(registration);
    }

    [Fact]
    public async Task UnconfiguredJwksReturnsSafeUnavailableWithoutPrivateMaterial()
    {
        using var factory = Factory(null);
        using var client = Client(factory);
        using var response = await client.GetAsync("/.well-known/jwks.json");
        await AssertUnavailableAsync(response);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("identity_unavailable", body.RootElement.GetProperty("code").GetString());
        Assert.False(body.RootElement.TryGetProperty("keys", out _));
    }

    [Theory]
    [InlineData("/api/v1/auth/logout", "anonymous")]
    [InlineData("/api/v1/auth/logout", "google-bearer")]
    [InlineData("/api/v1/auth/logout", "spoofed-headers")]
    [InlineData("/api/v1/auth/google/link", "anonymous")]
    [InlineData("/api/v1/auth/google/link", "google-bearer")]
    [InlineData("/api/v1/auth/google/link", "spoofed-headers")]
    public async Task ProtectedAuthenticationEndpointsRequireVerifiedApplicationBearer(string route, string condition)
    {
        using var factory = Factory(null);
        using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(new
            {
                refreshToken = "fictional-refresh-token",
                idToken = "fictional-google-id-token",
                currentPassword = "fictional-current-password"
            })
        };
        if (condition == "google-bearer")
        {
            var googleToken = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer: "https://accounts.google.com", audience: "fictional-google-client.apps.googleusercontent.com",
                claims: [new Claim("sub", "google-provider-subject"), new Claim("roles", "Admin")],
                notBefore: DateTime.UtcNow.AddMinutes(-1), expires: DateTime.UtcNow.AddMinutes(5)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", googleToken);
        }
        else if (condition == "spoofed-headers")
        {
            request.Headers.Add("X-User-Id", Guid.NewGuid().ToString("D"));
            request.Headers.Add("X-User-Roles", "Admin");
            request.Headers.Add("X-User-Role", "Admin");
            request.Headers.Add("X-Admin", "true");
            request.Headers.Add("X-Api-Key", "fictional-api-key");
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_credentials", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("The supplied credentials are invalid.", body.RootElement.GetProperty("title").GetString());
        Assert.False(body.RootElement.TryGetProperty("accessToken", out _));
        Assert.False(body.RootElement.TryGetProperty("refreshToken", out _));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static WebApplicationFactory<SwiftDrop.Identity.Api.Program> Factory(string? connectionString) =>
        new WebApplicationFactory<SwiftDrop.Identity.Api.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString,
                ["Identity:Tokens:Issuer"] = null,
                ["Identity:Tokens:Audience"] = null,
                ["Identity:Tokens:KeyId"] = null,
                ["Identity:Tokens:PrivateKeyPath"] = null,
                ["Logging:EventLog:LogLevel:Default"] = "None"
            }));
        });

    private static HttpClient Client(WebApplicationFactory<SwiftDrop.Identity.Api.Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://identity.example.test"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });

    private static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain(FictionalPassword, body);
        Assert.DoesNotContain("identity_readiness_test", body);
        Assert.DoesNotContain("UnsupportedIdentityConnectionProperty", body);
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("Npgsql", body);
        Assert.DoesNotContain("PRIVATE KEY", body);
        Assert.DoesNotContain("Password=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessToken", body);
        Assert.DoesNotContain("refreshToken", body);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }
}
