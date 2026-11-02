using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Authentication;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.ClientHarness;

public enum BookingHarnessActor { CustomerA, CustomerB, Courier }

public sealed record BookingHarnessZone(Guid Id, string Name);
public sealed record BookingHarnessContext(Guid ActorId, IReadOnlyList<string> Roles,
    IReadOnlyList<BookingHarnessZone> Zones);

/// <summary>
/// Socket-capable client test fixture. It owns a disposable PostgreSQL cluster,
/// real Delivery hosts and fictional identities; no normal host registers it.
/// </summary>
public sealed class BookingClientHarness : IAsyncDisposable
{
    private readonly PostgresFixture _database = new();
    private readonly List<BookingClientActorHost> _hosts = [];
    private bool _disposed;
    private static readonly BookingHarnessZone[] FixtureZones =
    [
        new(new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1"), "Test North"),
        new(new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa2"), "Test South")
    ];

    private BookingClientHarness() { }

    public BookingClientActorHost Host { get; private set; } = null!;
    public Uri Address => Host.Address;
    public BookingHarnessContext Context => Host.Context;
    internal string ConnectionString => _database.ConnectionString;

    public static async Task<BookingClientHarness> StartAsync(
        BookingHarnessActor actor = BookingHarnessActor.CustomerA, int port = 0)
    {
        Validate(actor, port);
        var harness = new BookingClientHarness();
        try
        {
            await harness._database.InitializePrivateAsync();
            await harness.SeedAsync();
            harness.Host = harness.StartActor(actor, port);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    /// <summary>Starts another server-selected actor against this fixture's data.</summary>
    public BookingClientActorHost StartActor(BookingHarnessActor actor, int port = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Validate(actor, port);
        var actorId = actor switch
        {
            BookingHarnessActor.CustomerA => DeliveryTestAuthentication.CustomerId,
            BookingHarnessActor.CustomerB => DeliveryTestAuthentication.OtherCustomerId,
            _ => DeliveryTestAuthentication.NonCustomerId
        };
        string[] roles = [actor == BookingHarnessActor.Courier ? "Courier" : "Customer"];
        var context = new BookingHarnessContext(actorId, roles, FixtureZones);
        var principal = DeliveryTestAuthentication.Principal(actorId.ToString("D"), roles);
        var rootFactory = new QuoteApiFactory(ConnectionString, principal, configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IStartupFilter>(new HarnessContextFilter(context));
        });
        var factory = rootFactory.WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(FindApiRoot());
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                // Discard normal endpoints and private configuration. The only
                // database is the explicitly injected owned test cluster above.
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = "",
                    ["DELIVERY_DATABASE_CONNECTION_STRING"] = "",
                    ["Logging:LogLevel:Default"] = "None",
                    ["Logging:EventLog:LogLevel:Default"] = "None"
                });
            });
        });
        try
        {
            factory.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
            factory.StartServer();
            var addresses = factory.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = new Uri(addresses?.Single()
                ?? throw new InvalidOperationException("The client fixture did not bind one HTTP address."));
            if (!address.IsLoopback || address.Scheme != Uri.UriSchemeHttp)
            {
                throw new InvalidOperationException("The client fixture must bind HTTP loopback only.");
            }

            var host = new BookingClientActorHost(rootFactory, address, context);
            _hosts.Add(host);
            return host;
        }
        catch
        {
            rootFactory.Dispose();
            throw;
        }
    }

    internal DeliveryDbContext OpenContext() => new(new DbContextOptionsBuilder<DeliveryDbContext>()
        .UseNpgsql(ConnectionString).Options);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? failures = null;
        try
        {
            foreach (var host in _hosts)
            {
                try { await host.DisposeAsync(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
        }
        finally
        {
            await _database.DisposeAsync();
        }

        if (failures is not null) throw new AggregateException("Client fixture host cleanup failed.", failures);
    }

    private async Task SeedAsync()
    {
        await using var database = OpenContext();
        await database.Database.MigrateAsync();
        foreach (var zone in FixtureZones)
        {
            database.Zones.Add(new DeliveryZone(zone.Id, zone.Name));
            foreach (var size in Enum.GetValues<ParcelSize>())
            {
                // Fictional prices live only in this disposable database. Both
                // clients receive their quote from the actual pricing handler.
                database.PricingTiers.Add(new PricingTier(Guid.NewGuid(), zone.Id, size,
                    "client-fixture-v1", 450m + (int)size * 150m, 60m));
            }
        }

        await database.SaveChangesAsync();
    }

    private static void Validate(BookingHarnessActor actor, int port)
    {
        if (!Enum.IsDefined(actor)) throw new ArgumentOutOfRangeException(nameof(actor));
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
    }

    private static string FindApiRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SwiftDrop.sln")))
            {
                return Path.Combine(directory.FullName, "src", "Services", "Delivery", "SwiftDrop.Delivery.Api");
            }
        }

        throw new InvalidOperationException("Run the client test fixture from the backend checkout.");
    }

    private sealed class HarnessContextFilter(BookingHarnessContext context) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (HttpContext request, RequestDelegate following) =>
            {
                if (request.Request.Path == "/__test/booking-context")
                {
                    if (!HttpMethods.IsGet(request.Request.Method))
                    {
                        request.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                        request.Response.Headers.Allow = "GET";
                        return;
                    }

                    request.Response.Headers.CacheControl = "no-store";
                    await request.Response.WriteAsJsonAsync(context);
                    return;
                }

                await following(request);
            });
            next(application);
        };
    }
}

public sealed class BookingClientActorHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<SwiftDrop.Delivery.Api.Program> _factory;
    private bool _disposed;

    internal BookingClientActorHost(WebApplicationFactory<SwiftDrop.Delivery.Api.Program> factory,
        Uri address, BookingHarnessContext context) => (_factory, Address, Context) = (factory, address, context);

    public Uri Address { get; }
    public BookingHarnessContext Context { get; }

    public HttpClient CreateClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = Address,
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _factory.DisposeAsync();
    }
}
