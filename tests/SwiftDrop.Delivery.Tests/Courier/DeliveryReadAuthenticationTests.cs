using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SwiftDrop.Delivery.Api.Infrastructure.Authentication;
using SwiftDrop.Delivery.Tests.Authentication;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Courier;

public sealed class DeliveryReadAuthenticationTests
{
    private const string Manifest = "/api/v1/couriers/me/assignments";
    private const string Detail = "/api/v1/parcels/11111111-1111-4111-8111-111111111111";

    [Theory]
    [InlineData(Manifest)]
    [InlineData(Detail)]
    public async Task AnonymousReadsReturn401WithoutDatabaseAccess(string path)
    {
        using var factory = new QuoteApiFactory(null);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task DetailRejectsClientSuppliedCourierScopeBeforeStorage()
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), "Courier"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Detail + "?courierId=" + Guid.NewGuid());
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }

    [Theory]
    [InlineData("Courier", true, false, false)]
    [InlineData("Customer", false, true, false)]
    [InlineData("Dispatcher", false, false, true)]
    [InlineData("Admin", false, false, true)]
    [InlineData("Merchant", false, false, false)]
    public void RolesComeFromTheOneAuthenticatedIdentity(string role, bool courier, bool customer, bool operations)
    {
        var id = Guid.NewGuid();
        var identity = new ClaimsIdentity([new("sub", id.ToString()), new("verified-role", role)],
            "verified", ClaimTypes.Name, "verified-role");
        var current = Current(new ClaimsPrincipal(identity));
        Assert.Equal(id, current.UserId);
        Assert.True(current.IsAuthenticated);
        Assert.Equal(courier, current.IsCourier);
        Assert.Equal(customer, current.IsCustomer);
        Assert.Equal(operations, current.IsOperations);
    }

    [Fact]
    public void RolesClaimIsSupportedWithoutBorrowingUnverifiedClaims()
    {
        var id = Guid.NewGuid();
        var trusted = new ClaimsIdentity([new("sub", id.ToString()), new("roles", "Courier")], "verified");
        var untrusted = new ClaimsIdentity([new("sub", Guid.NewGuid().ToString()), new("roles", "Admin")]);
        var current = Current(new ClaimsPrincipal([untrusted, trusted]));
        Assert.Equal(id, current.UserId);
        Assert.True(current.IsCourier);
        Assert.False(current.IsOperations);
        var customer = new ClaimsIdentity([new("sub", id.ToString()), new("roles", "Customer")], "verified");
        current = Current(new ClaimsPrincipal([customer, new ClaimsIdentity([new("roles", "Courier")])]));
        Assert.False(current.IsCourier);
    }

    [Fact]
    public void MultipleAuthenticatedIdentitiesCannotCombineSubjectsAndRoles()
    {
        var id = Guid.NewGuid().ToString();
        var current = Current(new ClaimsPrincipal([
            new ClaimsIdentity([new("sub", id)], "first"),
            new ClaimsIdentity([new("sub", id), new("roles", "Admin"), new("roles", "Courier")], "second")]));
        Assert.False(current.IsAuthenticated);
        Assert.Null(current.UserId);
        Assert.False(current.IsCourier);
        Assert.False(current.IsOperations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void MissingOrInvalidSubjectsCannotEstablishAnActor(string? subject)
    {
        var current = Current(DeliveryTestAuthentication.Principal(subject, "Courier"));
        Assert.Null(current.UserId);
    }

    [Fact]
    public void DuplicateSubjectsAreRejectedEvenWhenEqual()
    {
        var subject = Guid.NewGuid().ToString();
        var current = Current(new ClaimsPrincipal(new ClaimsIdentity([
            new("sub", subject), new("sub", subject), new(ClaimTypes.Role, "Courier")], "verified")));
        Assert.Null(current.UserId);
    }

    [Theory]
    [InlineData("Development", Manifest)]
    [InlineData("Production", Manifest)]
    [InlineData("Development", Detail)]
    [InlineData("Production", Detail)]
    public async Task ProductionAuthenticationFailsClosedDespiteForgedHeadersAndBearer(string environment, string path)
    {
        using var factory = new QuoteApiFactory(null, replaceAuthentication: false, environmentName: environment);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-User-Role", "Courier");
        client.DefaultRequestHeaders.Add("Authorization", "Bearer unverified-token");
        using var response = await client.GetAsync(path);
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
    }

    [Theory]
    [InlineData("Customer", Manifest, 403)]
    [InlineData("Dispatcher", Manifest, 403)]
    [InlineData("Admin", Manifest, 403)]
    [InlineData("Merchant", Manifest, 403)]
    [InlineData("Merchant", Detail, 403)]
    [InlineData("Courier", Manifest, 503)]
    [InlineData("Courier", Detail, 503)]
    [InlineData("Customer", Detail, 503)]
    public async Task RoleAuthorizationPrecedesUnavailableDatabase(string role, string path, int expected)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        await AssertProblemAsync(response, (HttpStatusCode)expected, expected == 403 ? "forbidden" : "delivery_unavailable");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidAuthenticatedCourierSubjectReturns401BeforeQueryValidation(string? subject)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(subject, "Courier"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Manifest + "?page=invalid");
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Theory]
    [InlineData("date=")]
    [InlineData("date=2026-02-30")]
    [InlineData("date=23-10-2026")]
    [InlineData("date=2026-10-23T00:00:00Z")]
    [InlineData("includeOverdue=yes")]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("page=")]
    [InlineData("page=2147483648")]
    [InlineData("page=2147483647&pageSize=100")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=1&page=2")]
    [InlineData("courierId=11111111-1111-4111-8111-111111111111")]
    public async Task InvalidQueriesReturnSafe400BeforeDatabaseAccess(string query)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), "Courier"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Manifest + "?" + query);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }

    private static ClaimsCurrentDeliveryUser Current(ClaimsPrincipal principal) =>
        new(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } });

    internal static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("correlationId").GetString()));
        Assert.Equal("https://example.invalid/problems/" + code, document.RootElement.GetProperty("type").GetString());
    }
}
