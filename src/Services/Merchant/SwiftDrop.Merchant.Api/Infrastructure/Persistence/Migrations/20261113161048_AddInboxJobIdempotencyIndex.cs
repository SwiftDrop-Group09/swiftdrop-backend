using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftDrop.Merchant.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxJobIdempotencyIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_inbox_jobs_business_id_idempotency_key",
                table: "inbox_jobs",
                columns: new[] { "business_id", "idempotency_key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_inbox_jobs_business_id_idempotency_key",
                table: "inbox_jobs");
        }
    }
}
