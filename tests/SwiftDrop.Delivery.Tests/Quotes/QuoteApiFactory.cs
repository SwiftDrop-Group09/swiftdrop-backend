using System.Security.Claims;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Authentication;

namespace SwiftDrop.Delivery.Tests.Quotes;

internal sealed class QuoteApiFactory(string? connectionString, ClaimsPrincipal? principal = null,
    bool replaceAuthentication = true, Action<IServiceCollection>? configureServices = null,
    string environmentName = "Testing")
    : WebApplicationFactory<SwiftDrop.Delivery.Api.Program>
{
    public static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    public static ClaimsPrincipal Customer(Guid ownerId) => DeliveryTestAuthentication.Customer(ownerId);

    public static ClaimsPrincipal Principal(string? subject, string role) =>
        DeliveryTestAuthentication.Principal(subject, role);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environmentName);
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
                DeliveryTestAuthentication.AddFixedPrincipal(services, principal);
            }

            configureServices?.Invoke(services);
        });
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
