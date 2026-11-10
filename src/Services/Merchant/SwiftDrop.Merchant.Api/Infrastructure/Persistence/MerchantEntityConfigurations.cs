using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Merchant.Api.Domain;

namespace SwiftDrop.Merchant.Api.Infrastructure.Persistence;

internal static partial class MerchantEntityConfigurations
{
    private const string StatusValues = "'Pending','Approved','Rejected','Suspended'";

    public static void Configure(ModelBuilder model)
    {
        var businesses = model.Entity<Business>();
        businesses.ToTable("businesses", table =>
        {
            table.HasCheckConstraint("ck_businesses_name", "btrim(business_name) <> ''");
            table.HasCheckConstraint("ck_businesses_email", "btrim(contact_email) <> ''");
            table.HasCheckConstraint("ck_businesses_status", $"status IN ({StatusValues})");
        });

        businesses.HasKey(x => x.Id);
        businesses.Property(x => x.Id).ValueGeneratedNever();
        businesses.Property(x => x.UserId).IsRequired();
        businesses.Property(x => x.BusinessName).HasMaxLength(200).IsRequired();
        businesses.Property(x => x.ContactEmail).HasMaxLength(254).IsRequired();
        businesses.Property(x => x.ContactPhone).HasMaxLength(32).IsRequired();
        businesses.Property(x => x.PickupAddress).HasMaxLength(500).IsRequired();
        businesses.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        businesses.Property(x => x.RejectionReason).HasMaxLength(500);
        businesses.Property(x => x.RegisteredAt).IsRequired();

        businesses.HasIndex(x => x.UserId);
        businesses.HasIndex(x => x.Status);

        var apiKeys = model.Entity<ApiKey>();
        apiKeys.ToTable("api_keys", table =>
        {
            table.HasCheckConstraint("ck_api_keys_prefix", "btrim(key_prefix) <> ''");
            table.HasCheckConstraint("ck_api_keys_hash", "btrim(key_hash) <> ''");
            table.HasCheckConstraint("ck_api_keys_scopes", "btrim(scopes) <> ''");
        });

        apiKeys.HasKey(x => x.Id);
        apiKeys.Property(x => x.Id).ValueGeneratedNever();
        apiKeys.Property(x => x.KeyPrefix).HasMaxLength(64).IsRequired();
        apiKeys.Property(x => x.KeyHash).HasMaxLength(128).IsRequired();
        apiKeys.Property(x => x.Salt).HasMaxLength(64).IsRequired();
        apiKeys.Property(x => x.Scopes).HasMaxLength(200).IsRequired();
        apiKeys.Property(x => x.IsActive).IsRequired();
        apiKeys.Property(x => x.CreatedAt).IsRequired();

        apiKeys.HasIndex(x => x.KeyHash).IsUnique();
        apiKeys.HasIndex(x => x.BusinessId);

        apiKeys.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        // Webhook entities configuration
        Webhooks.Persistence.WebhookEntityConfigurations.Configure(model);

        // Standard snake_case column mapping across all entities
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(SnakeCase(property.Name));
            }
        }
    }

    private static string SnakeCase(string value) => WordBoundary().Replace(value, "$1_$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();
}
