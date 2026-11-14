using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Infrastructure.Orders;
using SwiftDrop.Merchant.Api.Infrastructure.Orders.Delivery;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;
using static SwiftDrop.Merchant.Tests.Orders.Processing.ProcessingTestData;

namespace SwiftDrop.Merchant.Tests.Orders.Processing;

public sealed class DeliveryClientTests
{
    // ── Fake client behaves like the contract ──────────────────────────────

    [Fact]
    public async Task Fake_CreatesOnceAndFindsTheSameParcelAfterwards()
    {
        var fake = new FakeDeliveryBookingClient(new ManualTimeProvider(Now));
        var command = CreateBookingCommand.FromJob(Job());

        var created = Assert.IsType<BookingCallResult.Found>(await fake.CreateBookingAsync(command, CancellationToken.None));
        var replayed = Assert.IsType<BookingCallResult.Found>(await fake.CreateBookingAsync(command, CancellationToken.None));
        var found = Assert.IsType<BookingCallResult.Found>(
            await fake.GetByExternalOrderAsync(command.MerchantId, command.ExternalOrderId, CancellationToken.None));

        Assert.Equal(created.Booking.ParcelId, replayed.Booking.ParcelId);
        Assert.Equal(created.Booking.ParcelId, found.Booking.ParcelId);
        Assert.StartsWith("SD-DEV-", created.Booking.TrackingNumber, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_PricesWithTheContractExampleTariff()
    {
        var fake = new FakeDeliveryBookingClient(new ManualTimeProvider(Now));

        var result = Assert.IsType<BookingCallResult.Found>(
            await fake.CreateBookingAsync(CreateBookingCommand.FromJob(Job()), CancellationToken.None));

        Assert.Equal(540.00m, result.Booking.Total); // 450 + 60 × (2.5 − 1)
        Assert.Equal("LKR", result.Booking.Currency);
        Assert.Equal("2026-11-v1", result.Booking.PricingVersion);
    }

    [Fact]
    public async Task Fake_RejectsTheSameOrderWithDifferentContent()
    {
        var fake = new FakeDeliveryBookingClient(new ManualTimeProvider(Now));
        var command = CreateBookingCommand.FromJob(Job());
        await fake.CreateBookingAsync(command, CancellationToken.None);

        var changed = await fake.CreateBookingAsync(command with { RequestFingerprint = new string('a', 64) }, CancellationToken.None);

        Assert.Equal(new BookingCallResult.Rejected(DeliveryErrorCodes.OrderConflict), changed);
    }

    [Fact]
    public async Task Fake_UnknownOrderIsNotFound() =>
        Assert.IsType<BookingCallResult.NotFound>(await new FakeDeliveryBookingClient(new ManualTimeProvider(Now))
            .GetByExternalOrderAsync(Guid.NewGuid(), "ORD-NONE", CancellationToken.None));

    [Fact]
    public async Task Unconfigured_KeepsOrdersRetryingInsteadOfFakingSuccess()
    {
        var client = new UnconfiguredDeliveryBookingClient();

        var result = await client.CreateBookingAsync(CreateBookingCommand.FromJob(Job()), CancellationToken.None);

        Assert.Equal(new BookingCallResult.Transient(DeliveryErrorCodes.NotConfigured), result);
    }

    // ── Registration guards ────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "Development", typeof(UnconfiguredDeliveryBookingClient))]
    [InlineData("Fake", "Development", typeof(FakeDeliveryBookingClient))]
    [InlineData("Fake", "Testing", typeof(FakeDeliveryBookingClient))]
    public void Registration_PicksTheConfiguredClient(string? mode, string environment, Type expected)
    {
        using var provider = BuildServices(mode, environment).BuildServiceProvider();

        Assert.IsType(expected, provider.GetRequiredService<IDeliveryBookingClient>());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Registration_RefusesTheFakeOutsideDevelopment(string environment)
    {
        var error = Assert.Throws<InvalidOperationException>(() => BuildServices("Fake", environment));

        Assert.Contains(environment, error.Message, StringComparison.Ordinal);
    }

    private static ServiceCollection BuildServices(string? mode, string environment)
    {
        var settings = new Dictionary<string, string?>();
        if (mode is not null)
        {
            settings[OrderProcessingServiceCollectionExtensions.DeliveryClientSetting] = mode;
        }

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddMerchantPersistence(new ConfigurationBuilder().Build());
        services.AddMerchantOrderProcessing(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new TestHostEnvironment(environment));
        return services;
    }

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "SwiftDrop.Merchant.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
