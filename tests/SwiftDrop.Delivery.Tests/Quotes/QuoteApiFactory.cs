using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Tests.Quotes;

internal sealed class QuoteApiFactory(string? connectionString, ClaimsPrincipal? principal = null,
    bool replaceAuthentication = true, Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<SwiftDrop.Delivery.Api.Program>
{
    public static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private const string TestScheme = "QuoteTests";

    public static ClaimsPrincipal Customer(Guid ownerId) => Principal(ownerId.ToString(), "Customer");

    public static ClaimsPrincipal Principal(string? subject, string role)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        if (subject is not null)
        {
            claims.Add(new Claim("sub", subject));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, TestScheme));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = "",
                ["DELIVERY_DATABASE_CONNECTION_STRING"] = "",
                ["Logging:EventLog:LogLevel:Default"] = "None"
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider());
            if (connectionString is not null)
            {
                services.RemoveAll<DeliveryDbContext>();
                services.RemoveAll<DbContextOptions<DeliveryDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<DeliveryDbContext>>();
                services.AddDbContext<DeliveryDbContext>(options => options.UseNpgsql(connectionString));
            }

            if (replaceAuthentication)
            {
                services.AddSingleton(new FixedPrincipal(principal));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestScheme;
                    options.DefaultChallengeScheme = TestScheme;
                    options.DefaultForbidScheme = TestScheme;
                }).AddScheme<AuthenticationSchemeOptions, FixedPrincipalHandler>(TestScheme, _ => { });
            }

            configureServices?.Invoke(services);
        });
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record FixedPrincipal(ClaimsPrincipal? Value);

    private sealed class FixedPrincipalHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, FixedPrincipal principal)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            principal.Value?.Identity?.IsAuthenticated == true
                ? AuthenticateResult.Success(new AuthenticationTicket(principal.Value, TestScheme))
                : AuthenticateResult.NoResult());
    }
}
