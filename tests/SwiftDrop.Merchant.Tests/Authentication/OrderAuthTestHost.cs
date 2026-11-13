using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication.ApiKeys;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Tests.Authentication;

/// <summary>
/// Runs the application's real authentication handlers and authorization
/// policies (exactly as the authorization middleware does) against an
/// in-memory Merchant database. The "owner JWT" is the test scheme's fixed principal.
/// </summary>
internal sealed class OrderAuthTestHost : IDisposable
{
    private readonly MerchantApiFactory _factory;

    public OrderAuthTestHost(ClaimsPrincipal? ownerPrincipal = null) =>
        _factory = new MerchantApiFactory(ownerPrincipal, inMemoryDbName: $"order-auth-{Guid.NewGuid():N}",
            configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new SteppingTimeProvider());
            });

    public HttpClient CreateClient() => _factory.CreateClient();

    public async Task<Business> SeedBusinessAsync(BusinessStatus status, Guid? ownerUserId = null)
    {
        var business = Business.Register(ownerUserId ?? Guid.NewGuid(), "Fictional Traders", "ops@fictional.example",
            "+94770000000", "1 Fictional Road, Colombo");
        switch (status)
        {
            case BusinessStatus.Approved:
                business.Approve(MerchantApiFactory.AdminId);
                break;
            case BusinessStatus.Suspended:
                business.Approve(MerchantApiFactory.AdminId);
                business.Suspend(MerchantApiFactory.AdminId, "Fictional suspension");
                break;
            case BusinessStatus.Rejected:
                business.Reject(MerchantApiFactory.AdminId, "Fictional rejection");
                break;
        }

        await SaveAsync(business);
        return business;
    }

    public async Task<string> SeedKeyAsync(Guid businessId, bool revoked = false, params string[] scopes)
    {
        var (key, plaintext) = ApiKey.Create(businessId, scopes);
        if (revoked)
        {
            key.Revoke();
        }

        await SaveAsync(key);
        return plaintext;
    }

    /// <summary>Authenticates and authorizes a request for <paramref name="policyName"/>, returning the outcome and resolved caller.</summary>
    public async Task<(PolicyAuthorizationResult Result, OrderCaller Caller)> AuthorizeAsync(
        string policyName, Action<HttpRequest>? configureRequest = null)
    {
        using var scope = _factory.Services.CreateScope();
        var context = NewHttpContext(scope.ServiceProvider, configureRequest);

        var policy = await scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(policyName)
            ?? throw new InvalidOperationException($"Policy '{policyName}' is not registered.");
        var evaluator = scope.ServiceProvider.GetRequiredService<IPolicyEvaluator>();

        var authentication = await evaluator.AuthenticateAsync(policy, context);
        var result = await evaluator.AuthorizeAsync(policy, authentication, context, context);
        var caller = await scope.ServiceProvider.GetRequiredService<ICurrentOrderCaller>().ResolveAsync(CancellationToken.None);
        return (result, caller);
    }

    /// <summary>Runs only the API key scheme.</summary>
    public async Task<AuthenticateResult> AuthenticateApiKeyAsync(Action<HttpRequest> configureRequest)
    {
        using var scope = _factory.Services.CreateScope();
        var context = NewHttpContext(scope.ServiceProvider, configureRequest);
        return await context.AuthenticateAsync(ApiKeyAuthenticationDefaults.SchemeName);
    }

    public async Task<SwiftDrop.Merchant.Api.Domain.Orders.InboxJob?> FindJobAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MerchantDbContext>();
        return await database.InboxJobs.FindAsync(id);
    }

    public static Action<HttpRequest> WithKey(string key) =>
        request => request.Headers[ApiKeyAuthenticationDefaults.HeaderName] = key;

    public void Dispose() => _factory.Dispose();

    private static DefaultHttpContext NewHttpContext(IServiceProvider services, Action<HttpRequest>? configureRequest)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        configureRequest?.Invoke(context.Request);
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        return context;
    }

    /// <summary>Deterministic clock: every reading is one second after the previous one, so ordering by time is stable.</summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    private async Task SaveAsync(object entity)
    {
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MerchantDbContext>();
        database.Add(entity);
        await database.SaveChangesAsync();
    }
}
