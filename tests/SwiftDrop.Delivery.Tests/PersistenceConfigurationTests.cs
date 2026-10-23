using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Tests;

public sealed class PersistenceConfigurationTests
{
    [Fact]
    public void MissingDatabaseConfigurationFailsWhenPersistenceIsRequested()
    {
        var services = new ServiceCollection();
        services.AddDeliveryPersistence(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var error = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<DeliveryDbContext>());

        Assert.Contains("ConnectionStrings:Database", error.Message);
    }

    [Theory]
    [InlineData("swiftdrop_identity")]
    [InlineData("swiftdrop_merchant")]
    [InlineData("swiftdrop_notifications")]
    [InlineData("")]
    public void ConfigurationRejectsOtherServiceDatabases(string database)
    {
        Assert.Throws<InvalidOperationException>(() => DeliveryPersistence.RequireDeliveryDatabase(
            $"Host=example.invalid;Database={database};Username=fictional"));
    }

    [Fact]
    public void ConfigurationAcceptsOnlyDeliveryDatabaseWithoutOpeningConnection()
    {
        var connection = DeliveryPersistence.RequireDeliveryDatabase(
            "Host=example.invalid;Database=swiftdrop_delivery;Username=fictional");

        Assert.Contains("swiftdrop_delivery", connection);
    }

    [Fact]
    public void NamedDeliverySecretConfiguresPersistenceWithoutOpeningConnection()
    {
        var settings = new Dictionary<string, string?>
        {
            ["DELIVERY_DATABASE_CONNECTION_STRING"] =
                "Host=delivery.example.invalid;Database=swiftdrop_delivery;Username=fictional",
            ["IDENTITY_DATABASE_CONNECTION_STRING"] =
                "Host=identity.example.invalid;Database=swiftdrop_identity;Username=fictional"
        };
        var services = new ServiceCollection();
        services.AddDeliveryPersistence(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var database = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();

        Assert.Contains("delivery.example.invalid", database.Database.GetConnectionString());
    }

    [Fact]
    public void ScopedConfigurationTakesPrecedenceOverNamedDeliverySecret()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] =
                "Host=scoped.example.invalid;Database=swiftdrop_delivery;Username=fictional",
            ["DELIVERY_DATABASE_CONNECTION_STRING"] =
                "Host=secret.example.invalid;Database=swiftdrop_delivery;Username=fictional"
        };
        var services = new ServiceCollection();
        services.AddDeliveryPersistence(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var database = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();

        Assert.Contains("scoped.example.invalid", database.Database.GetConnectionString());
    }

    [Fact]
    public void OtherServiceSecretCannotConfigureDelivery()
    {
        var settings = new Dictionary<string, string?>
        {
            ["IDENTITY_DATABASE_CONNECTION_STRING"] =
                "Host=identity.example.invalid;Database=swiftdrop_identity;Username=fictional"
        };
        var services = new ServiceCollection();
        services.AddDeliveryPersistence(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<DeliveryDbContext>());
    }

    [Fact]
    public void ConcurrencyErrorsHaveSafeClassification()
    {
        Assert.Equal(DeliveryDatabaseFailure.ConcurrentWrite,
            DeliveryDatabaseErrors.Classify(new DbUpdateConcurrencyException()));
        Assert.Equal(DeliveryDatabaseFailure.Unavailable,
            DeliveryDatabaseErrors.Classify(new TimeoutException()));
        Assert.Equal(DeliveryDatabaseFailure.Unknown,
            DeliveryDatabaseErrors.Classify(new InvalidOperationException()));
    }
}
