using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication.ApiKeys;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication.OrderAccess;
using static SwiftDrop.Merchant.Tests.Authentication.OrderAuthTestHost;

namespace SwiftDrop.Merchant.Tests.Authentication;

public sealed class OrderAuthorizationPolicyTests
{
    private const string Write = MerchantAuthorizationPolicies.OrdersWrite;
    private const string Read = MerchantAuthorizationPolicies.OrdersRead;

    // ── OrdersWrite: API key + orders:write + approved business ────────────

    [Fact]
    public async Task Write_KeyWithWriteScope_IsAllowedAndResolvesItsBusiness()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: ApiKeyScopes.OrdersWrite);

        var (result, caller) = await host.AuthorizeAsync(Write, WithKey(key));

        Assert.True(result.Succeeded);
        Assert.Equal(OrderCallerKind.ApiKey, caller.Kind);
        Assert.Equal(business.Id, caller.BusinessId);
        Assert.NotNull(caller.ApiKeyId);
    }

    [Fact]
    public async Task Write_NoCredential_IsChallenged()
    {
        using var host = new OrderAuthTestHost();

        var (result, caller) = await host.AuthorizeAsync(Write);

        Assert.True(result.Challenged);
        Assert.Equal(OrderCaller.Anonymous, caller);
    }

    [Fact]
    public async Task Write_InvalidOrRevokedKey_IsChallenged()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var revoked = await host.SeedKeyAsync(business.Id, revoked: true, ApiKeyScopes.OrdersWrite);

        var (revokedResult, _) = await host.AuthorizeAsync(Write, WithKey(revoked));
        var (malformedResult, _) = await host.AuthorizeAsync(Write, WithKey("swd_test_not-a-real-key"));

        Assert.True(revokedResult.Challenged);
        Assert.True(malformedResult.Challenged);
    }

    [Fact]
    public async Task Write_KeyWithoutWriteScope_IsForbidden()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: [ApiKeyScopes.OrdersRead, ApiKeyScopes.WebhooksManage]);

        var (result, _) = await host.AuthorizeAsync(Write, WithKey(key));

        Assert.True(result.Forbidden);
    }

    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Suspended)]
    [InlineData(BusinessStatus.Rejected)]
    public async Task Write_ValidKeyOfUnapprovedBusiness_IsForbidden(BusinessStatus status)
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(status);
        var key = await host.SeedKeyAsync(business.Id, scopes: ApiKeyScopes.OrdersWrite);

        var (result, caller) = await host.AuthorizeAsync(Write, WithKey(key));

        Assert.True(result.Forbidden);
        Assert.Equal(status, caller.BusinessStatus);
    }

    [Fact]
    public async Task Write_OwnerJwtAlone_IsChallenged()
    {
        using var host = new OrderAuthTestHost(MerchantApiFactory.Merchant());
        await host.SeedBusinessAsync(BusinessStatus.Approved, MerchantApiFactory.MerchantId);

        var (result, _) = await host.AuthorizeAsync(Write);

        Assert.True(result.Challenged);
    }

    // ── OrdersRead: owner JWT, or API key + orders:read; approved business ─

    [Fact]
    public async Task Read_ApprovedOwner_IsAllowed()
    {
        using var host = new OrderAuthTestHost(MerchantApiFactory.Merchant());
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved, MerchantApiFactory.MerchantId);

        var (result, caller) = await host.AuthorizeAsync(Read);

        Assert.True(result.Succeeded);
        Assert.Equal(OrderCallerKind.MerchantOwner, caller.Kind);
        Assert.Equal(business.Id, caller.BusinessId);
        Assert.Null(caller.ApiKeyId);
    }

    [Fact]
    public async Task Read_OwnerOfSuspendedBusiness_IsForbidden()
    {
        using var host = new OrderAuthTestHost(MerchantApiFactory.Merchant());
        await host.SeedBusinessAsync(BusinessStatus.Suspended, MerchantApiFactory.MerchantId);

        var (result, _) = await host.AuthorizeAsync(Read);

        Assert.True(result.Forbidden);
    }

    [Fact]
    public async Task Read_UserWithoutBusiness_IsForbidden()
    {
        using var host = new OrderAuthTestHost(MerchantApiFactory.Merchant());
        await host.SeedBusinessAsync(BusinessStatus.Approved, MerchantApiFactory.OtherId);

        var (result, caller) = await host.AuthorizeAsync(Read);

        Assert.True(result.Forbidden);
        Assert.Equal(OrderCaller.Anonymous, caller);
    }

    [Fact]
    public async Task Read_KeyWithReadScope_IsAllowed()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: ApiKeyScopes.OrdersRead);

        var (result, caller) = await host.AuthorizeAsync(Read, WithKey(key));

        Assert.True(result.Succeeded);
        Assert.Equal(business.Id, caller.BusinessId);
    }

    [Fact]
    public async Task Read_KeyWithOnlyWriteScope_IsForbidden()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: ApiKeyScopes.OrdersWrite);

        var (result, _) = await host.AuthorizeAsync(Read, WithKey(key));

        Assert.True(result.Forbidden);
    }

    [Fact]
    public async Task Read_OwnerJwtPlusApiKey_IsRejected()
    {
        using var host = new OrderAuthTestHost(MerchantApiFactory.Merchant());
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved, MerchantApiFactory.MerchantId);
        var key = await host.SeedKeyAsync(business.Id, scopes: ApiKeyScopes.OrdersRead);

        var (result, _) = await host.AuthorizeAsync(Read, request =>
        {
            request.Headers.Authorization = "Bearer fictional-token";
            request.Headers[ApiKeyAuthenticationDefaults.HeaderName] = key;
        });

        Assert.False(result.Succeeded);
    }

    // ── Business isolation (TC-SEC-03) ─────────────────────────────────────

    [Fact]
    public async Task EachKey_ResolvesOnlyToItsOwnBusiness()
    {
        using var host = new OrderAuthTestHost();
        var businessA = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var businessB = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var keyA = await host.SeedKeyAsync(businessA.Id, scopes: ApiKeyScopes.OrdersRead);
        var keyB = await host.SeedKeyAsync(businessB.Id, scopes: ApiKeyScopes.OrdersRead);

        var (_, callerA) = await host.AuthorizeAsync(Read, WithKey(keyA));
        var (_, callerB) = await host.AuthorizeAsync(Read, WithKey(keyB));

        Assert.Equal(businessA.Id, callerA.BusinessId);
        Assert.Equal(businessB.Id, callerB.BusinessId);
    }
}
