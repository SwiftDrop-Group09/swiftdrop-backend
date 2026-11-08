using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SwiftDrop.Merchant.Api.Infrastructure.Authentication;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Tests;

/// <summary>
/// WebApplicationFactory for merchant API integration tests.
/// Supplies a fixed principal and in-memory configuration; no real DB required.
/// </summary>
internal sealed class MerchantApiFactory(
    ClaimsPrincipal? principal = null,
    string? inMemoryDbName = null,
    bool replaceAuthentication = true,
    Action<IServiceCollection>? configureServices = null,
    string environmentName = "Testing")
    : WebApplicationFactory<SwiftDrop.Merchant.Api.Program>
{
    public const string TestSchemeName = "MerchantTestIdentity";

    public static readonly Guid AdminId = new("aaaaaaaa-0000-4000-8000-aaaaaaaaaaaa");
    public static readonly Guid MerchantId = new("bbbbbbbb-0000-4000-8000-bbbbbbbbbbbb");
    public static readonly Guid OtherId = new("cccccccc-0000-4000-8000-cccccccccccc");

    public static ClaimsPrincipal Principal(Guid userId, string role) =>
        new(new ClaimsIdentity(
            [new("sub", userId.ToString("D")), new("roles", role)],
            TestSchemeName));

    public static ClaimsPrincipal Admin() => Principal(AdminId, "Admin");
    public static ClaimsPrincipal Merchant() => Principal(MerchantId, "Merchant");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environmentName);
        builder.ConfigureTestServices(services =>
        {
            if (inMemoryDbName is not null)
            {
                services.RemoveAll<MerchantDbContext>();
                services.RemoveAll<DbContextOptions<MerchantDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<MerchantDbContext>>();
                services.AddDbContext<MerchantDbContext>(options =>
                    options.UseInMemoryDatabase(inMemoryDbName));
            }

            if (replaceAuthentication)
            {
                services.RemoveAll<IAuthenticationSchemeProvider>();
                services.AddAuthentication(TestSchemeName)
                    .AddScheme<AuthenticationSchemeOptions, FixedPrincipalHandler>(TestSchemeName, _ => { });
                services.AddSingleton<ClaimsPrincipal>(principal ?? new ClaimsPrincipal());
            }

            configureServices?.Invoke(services);
        });
    }

    /// <summary>Fixed-identity authentication handler — test environments only.</summary>
    private sealed class FixedPrincipalHandler(
        ClaimsPrincipal fixedPrincipal,
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!fixedPrincipal.Identities.Any(i => i.IsAuthenticated))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(fixedPrincipal, TestSchemeName)));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Headers.WWWAuthenticate = "Bearer";
            return base.HandleChallengeAsync(properties);
        }
    }
}
