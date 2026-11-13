using System.Net;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication.ApiKeys;
using static SwiftDrop.Merchant.Tests.Authentication.OrderAuthTestHost;

namespace SwiftDrop.Merchant.Tests.Authentication;

public sealed class ApiKeyAuthenticationTests
{
    [Fact]
    public async Task ValidKey_IssuesBusinessKeyAndScopeClaimsWithoutSubject()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: [ApiKeyScopes.OrdersWrite, ApiKeyScopes.OrdersRead]);

        var result = await host.AuthenticateApiKeyAsync(WithKey(key));

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal(business.Id.ToString("D"), principal.FindFirst(ApiKeyClaimTypes.BusinessId)?.Value);
        Assert.NotNull(principal.FindFirst(ApiKeyClaimTypes.ApiKeyId));
        Assert.Equal("api_key", principal.FindFirst(ApiKeyClaimTypes.AuthenticationMethod)?.Value);
        Assert.Equal(
            new[] { ApiKeyScopes.OrdersRead, ApiKeyScopes.OrdersWrite },
            principal.FindAll(ApiKeyClaimTypes.Scope).Select(claim => claim.Value).Order());
        Assert.Null(principal.FindFirst("sub"));
    }

    [Fact]
    public async Task MissingHeader_GivesNoResult()
    {
        using var host = new OrderAuthTestHost();

        var result = await host.AuthenticateApiKeyAsync(_ => { });

        Assert.True(result.None);
    }

    [Fact]
    public async Task WrongSecret_Fails()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: ApiKeyScopes.OrdersWrite);
        var tampered = key[..^4] + (key.EndsWith("0000", StringComparison.Ordinal) ? "1111" : "0000");

        var result = await host.AuthenticateApiKeyAsync(WithKey(tampered));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task UnknownKey_Fails()
    {
        using var host = new OrderAuthTestHost();
        var (_, neverStored) = ApiKey.Create(Guid.NewGuid(), [ApiKeyScopes.OrdersWrite]);

        var result = await host.AuthenticateApiKeyAsync(WithKey(neverStored));

        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task RevokedKey_Fails()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, revoked: true, ApiKeyScopes.OrdersWrite);

        var result = await host.AuthenticateApiKeyAsync(WithKey(key));

        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task TwoKeyHeaders_Fail()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: ApiKeyScopes.OrdersWrite);

        var result = await host.AuthenticateApiKeyAsync(request =>
            request.Headers[ApiKeyAuthenticationDefaults.HeaderName] = new[] { key, key });

        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task KeyTogetherWithAuthorizationHeader_Fails()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: ApiKeyScopes.OrdersWrite);

        var result = await host.AuthenticateApiKeyAsync(request =>
        {
            request.Headers[ApiKeyAuthenticationDefaults.HeaderName] = key;
            request.Headers.Authorization = "Bearer fictional-token";
        });

        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task FailureMessage_NeverContainsTheKey()
    {
        using var host = new OrderAuthTestHost();
        var (_, neverStored) = ApiKey.Create(Guid.NewGuid(), [ApiKeyScopes.OrdersWrite]);

        var result = await host.AuthenticateApiKeyAsync(WithKey(neverStored));

        Assert.DoesNotContain(neverStored, result.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingJwtRoutes_IgnoreValidApiKeys()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: [ApiKeyScopes.OrdersWrite, ApiKeyScopes.OrdersRead]);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, key);

        var profile = await client.GetAsync(new Uri("/api/v1/merchants/me", UriKind.Relative));
        var keys = await client.GetAsync(new Uri("/api/v1/merchants/api-keys", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, profile.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, keys.StatusCode);
    }
}
