using System.Net;
using System.Security.Claims;
using SwiftDrop.Delivery.Tests.Authentication;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Courier;

public sealed class CourierHistoryAuthenticationTests
{
    private const string Path = "/api/v1/couriers/me/history";

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task DeployedHostsRejectUnverifiedBearerAndForwardedIdentityBeforeQuery(string environment)
    {
        using var factory = new QuoteApiFactory(null, replaceAuthentication: false, environmentName: environment);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer unverified-history-token");
        client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-User-Role", "Courier");
        using var response = await client.GetAsync(Path + "?page=invalid&courierId=forged");
        await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Merchant")]
    [InlineData("Dispatcher")]
    [InlineData("Admin")]
    public async Task OtherRolesCannotAccessTheSelfHistoryOrOverrideItsScope(string role)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Path + "?page=invalid");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidSubjectsFailClosed(string? subject)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(subject, "Courier"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RoleFromAnotherIdentityCannotAuthorizeAnAuthenticatedCustomer()
    {
        var principal = new ClaimsPrincipal([
            new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Customer")], "trusted-test"),
            new ClaimsIdentity([new Claim(ClaimTypes.Role, "Courier")])
        ]);
        using var factory = new QuoteApiFactory(null, principal);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?page=2147483648")]
    [InlineData("?page=2147483647&pageSize=100")]
    [InlineData("?pageSize=101")]
    [InlineData("?page=1&page=2")]
    [InlineData("?Page=1")]
    [InlineData("?courierId=forged")]
    [InlineData("?status=Delivered")]
    [InlineData("?from=2026-01-01&to=2026-12-31")]
    [InlineData("?date=2026-10-29")]
    [InlineData("?includeOverdue=true")]
    public async Task OnlyDocumentedPageQueryIsAcceptedBeforeDatabaseAccess(string query)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), "Courier"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Path + query);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task ValidTrustedCourierReceivesSafeUnavailableWhenDatabaseIsMissing()
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), "Courier"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Path);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "delivery_unavailable");
        Assert.DoesNotContain("connection", await response.Content.ReadAsStringAsync());
    }
}
