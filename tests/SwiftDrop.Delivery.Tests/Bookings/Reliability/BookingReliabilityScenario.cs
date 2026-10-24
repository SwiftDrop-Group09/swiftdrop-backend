using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Bookings.Reliability;

internal sealed class BookingReliabilityScenario : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private BookingReliabilityScenario(PostgresFixture fixture, Action<IServiceCollection>? configure)
    {
        ApplicationName = "booking-reliability-" + Guid.NewGuid().ToString("N");
        ConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = ApplicationName
        }.ConnectionString;
        OwnerId = Guid.NewGuid();
        Clock = new ReliabilityClock();
        Pickup = new DeliveryZone(Guid.NewGuid(), "Reliability pickup " + Guid.NewGuid());
        Delivery = new DeliveryZone(Guid.NewGuid(), "Reliability delivery " + Guid.NewGuid());
        // Contract example data belongs only to this disposable test database.
        Tier = new PricingTier(Guid.NewGuid(), Delivery.Id, ParcelSize.Small, "test-reliability-v1", 450m, 60m);
        Factory = CreateFactory(configure);
        Client = Factory.CreateClient();
        Client.DefaultRequestHeaders.Add("X-Correlation-ID", ApplicationName);
    }

    public Guid OwnerId { get; }
    public string ConnectionString { get; }
    public string ApplicationName { get; }
    public QuoteApiFactory Factory { get; }
    public HttpClient Client { get; }
    public ReliabilityClock Clock { get; }
    public QuoteResponse Quote { get; private set; } = null!;
    public DeliveryZone Pickup { get; }
    public DeliveryZone Delivery { get; }
    public PricingTier Tier { get; }

    public static async Task<BookingReliabilityScenario> CreateAsync(
        PostgresFixture fixture, Action<IServiceCollection>? configure = null)
    {
        var scenario = new BookingReliabilityScenario(fixture, configure);
        try
        {
            await using var database = scenario.OpenContext();
            await database.Database.MigrateAsync();
            database.Zones.AddRange(scenario.Pickup, scenario.Delivery);
            database.PricingTiers.Add(scenario.Tier);
            await database.SaveChangesAsync();
            scenario.Quote = await scenario.CreateQuoteAsync();
            return scenario;
        }
        catch
        {
            await scenario.DisposeAsync();
            throw;
        }
    }

    public QuoteApiFactory CreateFactory(Action<IServiceCollection>? configure = null) =>
        new(ConnectionString, QuoteApiFactory.Customer(OwnerId), configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            configure?.Invoke(services);
        });

    public DeliveryDbContext OpenContext() => new(new DbContextOptionsBuilder<DeliveryDbContext>()
        .UseNpgsql(ConnectionString).Options);

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<QuoteResponse> CreateQuoteAsync()
    {
        var input = new JsonObject
        {
            ["pickupAddress"] = Address(Pickup.Id, "Private fictional pickup street"),
            ["deliveryAddress"] = Address(Delivery.Id, "Private fictional delivery street"),
            ["packageDetails"] = new JsonObject { ["weightKg"] = 2.5m, ["size"] = "Small" },
            ["recipient"] = new JsonObject { ["name"] = "Private Fictional Recipient", ["phoneNumber"] = "0000000000" }
        };
        using var response = await Client.PostAsync("/api/v1/quotes",
            new StringContent(input.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<QuoteResponse>(await response.Content.ReadAsStringAsync(), Json)!;
    }

    public Task<HttpResponseMessage> ConfirmAsync(string key, Guid? quoteId = null, decimal? total = null,
        CancellationToken cancellationToken = default) =>
        ConfirmRawAsync(Body(quoteId ?? Quote.QuoteId, total ?? Quote.Total), key, cancellationToken);

    public async Task<HttpResponseMessage> ConfirmRawAsync(string body, string key,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/parcels")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        return await Client.SendAsync(request, cancellationToken);
    }

    public static string Body(Guid quoteId, decimal total) =>
        new JsonObject { ["quoteId"] = quoteId.ToString("D"), ["acceptTotal"] = total }.ToJsonString();

    public async Task<BookingRowCounts> CountsAsync()
    {
        await using var database = OpenContext();
        return new(await database.Quotes.CountAsync(), await database.Parcels.CountAsync(),
            await database.PriceSnapshots.CountAsync(), await database.BookingHistory.CountAsync(),
            await database.IdempotencyRecords.CountAsync(), await database.OutboxMessages.CountAsync());
    }

    public async Task<BookingGraph> GraphAsync(string key)
    {
        await using var database = OpenContext();
        var record = await database.IdempotencyRecords.AsNoTracking().SingleAsync(item =>
            item.PrincipalId == OwnerId.ToString("D") && item.Operation == "CreateParcel" && item.Key == key);
        var parcelId = Assert.IsType<Guid>(record.ParcelId);
        var parcel = await database.Parcels.AsNoTracking().SingleAsync(item => item.Id == parcelId);
        var quote = await database.Quotes.AsNoTracking().SingleAsync(item => item.ConsumedByParcelId == parcelId);
        var snapshot = await database.PriceSnapshots.AsNoTracking().SingleAsync(item => item.ParcelId == parcelId);
        var history = await database.BookingHistory.AsNoTracking().SingleAsync(item => item.ParcelId == parcelId);
        var outbox = Assert.Single(await database.OutboxMessages.AsNoTracking().ToArrayAsync(), message =>
        {
            using var payload = JsonDocument.Parse(message.Payload);
            return payload.RootElement.GetProperty("parcelId").GetGuid() == parcelId;
        });
        var response = Assert.IsType<StoredBookingResponse>(
            JsonSerializer.Deserialize<StoredBookingResponse>(record.StoredResponse!, Json));
        return new(quote, parcel, snapshot, history, record, outbox, response);
    }

    public async Task WaitForBlockedRequestAsync(CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        await using var connection = await OpenConnectionAsync();
        while (timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                    WHERE application_name = @application AND state = 'active' AND wait_event_type = 'Lock')
                """, connection);
            command.Parameters.AddWithValue("application", ApplicationName);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }

        Assert.Fail("The scenario's PostgreSQL request did not enter the expected lock wait.");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }

    private static JsonObject Address(Guid zone, string street) => new()
    {
        ["street"] = street, ["city"] = "Private fictional city", ["postalCode"] = "00000", ["zoneId"] = zone.ToString("D")
    };
}

internal sealed class ReliabilityClock : TimeProvider
{
    private long _utcTicks = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).AddTicks(9).UtcTicks;

    public DateTimeOffset UtcNow
    {
        get => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
    }

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed record BookingRowCounts(int Quotes, int Parcels, int Snapshots, int History, int Keys, int Outbox)
{
    public BookingRowCounts Booked() => this with
    {
        Parcels = Parcels + 1, Snapshots = Snapshots + 1, History = History + 1, Keys = Keys + 1, Outbox = Outbox + 1
    };
}

internal sealed record BookingGraph(Quote Quote, Parcel Parcel, PriceSnapshot Snapshot,
    BookingHistory History, IdempotencyRecord Record, OutboxMessage Outbox, StoredBookingResponse Response);
