using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Tests.Orders;

/// <summary>
/// Inspects the EF design-time model (the one migrations are generated from,
/// including check constraints); no database connection is opened.
/// </summary>
public sealed class InboxJobPersistenceModelTests
{
    private static IEntityType InboxJobEntity()
    {
        var options = new DbContextOptionsBuilder<MerchantDbContext>()
            .UseNpgsql("Host=mock.invalid;Database=swiftdrop_merchant")
            .Options;
        using var context = new MerchantDbContext(options);
        return context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(InboxJob))
            ?? throw new InvalidOperationException("InboxJob is not mapped.");
    }

    private static IProperty Property(IEntityType entity, string name) =>
        entity.FindProperty(name) ?? throw new InvalidOperationException($"{name} is not mapped.");

    [Fact]
    public void MapsToSnakeCaseInboxJobsTable()
    {
        var entity = InboxJobEntity();

        Assert.Equal("inbox_jobs", entity.GetTableName());
        Assert.Equal("business_id", Property(entity, nameof(InboxJob.BusinessId)).GetColumnName());
        Assert.Equal("external_order_id", Property(entity, nameof(InboxJob.ExternalOrderId)).GetColumnName());
        Assert.Equal("next_retry_at", Property(entity, nameof(InboxJob.NextRetryAt)).GetColumnName());
        Assert.Equal("last_refreshed_at", Property(entity, nameof(InboxJob.LastRefreshedAt)).GetColumnName());
    }

    [Fact]
    public void UsesExpectedColumnTypes()
    {
        var entity = InboxJobEntity();

        Assert.Equal("jsonb", Property(entity, nameof(InboxJob.OrderPayload)).GetColumnType());
        Assert.Equal("character(64)", Property(entity, nameof(InboxJob.RequestFingerprint)).GetColumnType());
        Assert.Equal("numeric(18,2)", Property(entity, nameof(InboxJob.Total)).GetColumnType());
        Assert.Equal(InboxJobLimits.ExternalOrderIdMaxLength, Property(entity, nameof(InboxJob.ExternalOrderId)).GetMaxLength());
        Assert.Equal(typeof(string), Property(entity, nameof(InboxJob.State)).GetProviderClrType());
    }

    [Fact]
    public void ExternalOrderIdIsUniquePerBusiness()
    {
        var entity = InboxJobEntity();

        var unique = Assert.Single(entity.GetIndexes(), index => index.IsUnique);
        Assert.Equal(
            new[] { nameof(InboxJob.BusinessId), nameof(InboxJob.ExternalOrderId) },
            unique.Properties.Select(property => property.Name));
    }

    [Fact]
    public void HasProcessorAndPagingIndexes()
    {
        var indexes = InboxJobEntity().GetIndexes()
            .Select(index => string.Join(',', index.Properties.Select(property => property.Name)))
            .ToList();

        Assert.Contains("State,NextRetryAt", indexes);
        Assert.Contains("BusinessId,CreatedAt,Id", indexes);
    }

    [Fact]
    public void BusinessWithOrdersCannotBeDeleted()
    {
        var foreignKey = Assert.Single(InboxJobEntity().GetForeignKeys());

        Assert.Equal(typeof(Business), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void DeclaresStateCheckConstraintFromEnum()
    {
        var constraint = Assert.Single(InboxJobEntity().GetCheckConstraints(), c => c.Name == "ck_inbox_jobs_state");

        foreach (var state in Enum.GetNames<InboxJobState>())
        {
            Assert.Contains($"'{state}'", constraint.Sql);
        }
    }
}
