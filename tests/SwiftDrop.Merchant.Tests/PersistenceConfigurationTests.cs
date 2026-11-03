using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Tests;

public sealed class PersistenceConfigurationTests
{
    [Fact]
    public void MissingDatabaseConfigurationFailsWhenPersistenceIsRequested()
    {
        var services = new ServiceCollection();
        services.AddMerchantPersistence(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var error = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<MerchantDbContext>());

        Assert.Contains("ConnectionStrings:Database", error.Message);
    }

    [Theory]
    [InlineData("swiftdrop_identity")]
    [InlineData("swiftdrop_delivery")]
    [InlineData("swiftdrop_notifications")]
    [InlineData("")]
    public void ConfigurationRejectsOtherServiceDatabases(string database)
    {
        Assert.Throws<InvalidOperationException>(() => MerchantPersistence.RequireMerchantDatabase(
            $"Host=example.invalid;Database={database};Username=fictional"));
    }

    [Fact]
    public void ConfigurationAcceptsOnlyMerchantDatabaseWithoutOpeningConnection()
    {
        var connection = MerchantPersistence.RequireMerchantDatabase(
            "Host=example.invalid;Database=swiftdrop_merchant;Username=fictional");

        Assert.Contains("swiftdrop_merchant", connection);
    }

    [Fact]
    public void NamedMerchantSecretConfiguresPersistenceWithoutOpeningConnection()
    {
        var settings = new Dictionary<string, string?>
        {
            ["MERCHANT_DATABASE_CONNECTION_STRING"] =
                "Host=merchant.example.invalid;Database=swiftdrop_merchant;Username=fictional",
            ["DELIVERY_DATABASE_CONNECTION_STRING"] =
                "Host=delivery.example.invalid;Database=swiftdrop_delivery;Username=fictional"
        };
        var services = new ServiceCollection();
        services.AddMerchantPersistence(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var database = scope.ServiceProvider.GetRequiredService<MerchantDbContext>();

        Assert.Contains("merchant.example.invalid", database.Database.GetConnectionString());
    }

    [Fact]
    public void ScopedConfigurationTakesPrecedenceOverNamedMerchantSecret()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] =
                "Host=scoped.example.invalid;Database=swiftdrop_merchant;Username=fictional",
            ["MERCHANT_DATABASE_CONNECTION_STRING"] =
                "Host=secret.example.invalid;Database=swiftdrop_merchant;Username=fictional"
        };
        var services = new ServiceCollection();
        services.AddMerchantPersistence(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var database = scope.ServiceProvider.GetRequiredService<MerchantDbContext>();

        Assert.Contains("scoped.example.invalid", database.Database.GetConnectionString());
    }

    [Fact]
    public void EntityConfigurationsModelEnforcesSchemaAndRelations()
    {
        var options = new DbContextOptionsBuilder<MerchantDbContext>()
            .UseNpgsql("Host=mock.invalid;Database=swiftdrop_merchant")
            .Options;

        using var context = new MerchantDbContext(options);
        var model = context.Model;

        var businessEntity = model.FindEntityType(typeof(Business));
        Assert.NotNull(businessEntity);
        Assert.Equal("businesses", businessEntity.GetTableName());

        var businessId = businessEntity.FindProperty("Id");
        Assert.NotNull(businessId);
        Assert.Equal("id", businessId.GetColumnName());
        Assert.True(businessEntity.FindPrimaryKey()?.Properties.Contains(businessId));

        var contactEmail = businessEntity.FindProperty("ContactEmail");
        Assert.NotNull(contactEmail);
        Assert.Equal("contact_email", contactEmail.GetColumnName());
        Assert.Equal(254, contactEmail.GetMaxLength());

        var apiKeyEntity = model.FindEntityType(typeof(ApiKey));
        Assert.NotNull(apiKeyEntity);
        Assert.Equal("api_keys", apiKeyEntity.GetTableName());

        var keyHash = apiKeyEntity.FindProperty("KeyHash");
        Assert.NotNull(keyHash);
        Assert.Equal("key_hash", keyHash.GetColumnName());

        var keyHashIndex = apiKeyEntity.GetIndexes().FirstOrDefault(i => i.Properties.Contains(keyHash));
        Assert.NotNull(keyHashIndex);
        Assert.True(keyHashIndex.IsUnique);

        var foreignKey = apiKeyEntity.GetForeignKeys()
            .FirstOrDefault(fk => fk.PrincipalEntityType == businessEntity);
        Assert.NotNull(foreignKey);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }
}

