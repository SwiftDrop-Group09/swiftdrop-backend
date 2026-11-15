using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftDrop.Merchant.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MerchantDeliveryFailureReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                table: "inbox_jobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbox_jobs_failure_reason",
                table: "inbox_jobs",
                sql: "failure_reason IS NULL OR (parcel_status = 'Failed' AND failure_reason IN ('RecipientUnavailable','AddressUnreachable','Damaged','Other'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_inbox_jobs_failure_reason",
                table: "inbox_jobs");

            migrationBuilder.DropColumn(
                name: "failure_reason",
                table: "inbox_jobs");
        }
    }
}
