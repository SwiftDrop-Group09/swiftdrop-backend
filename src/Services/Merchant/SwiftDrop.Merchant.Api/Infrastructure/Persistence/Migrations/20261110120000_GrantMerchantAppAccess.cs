using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

#nullable disable

namespace SwiftDrop.Merchant.Api.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(MerchantDbContext))]
[Migration("20261110120000_GrantMerchantAppAccess")]
public partial class GrantMerchantAppAccess : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("GRANT USAGE ON SCHEMA public TO merchant_app;");
        migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.businesses, public.api_keys TO merchant_app;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLE public.businesses, public.api_keys FROM merchant_app;");
    }
}
