using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftDrop.Merchant.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantOrderInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inbox_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_order_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    order_payload = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    parcel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tracking_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    accepted_price_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    parcel_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    parcel_version = table.Column<long>(type: "bigint", nullable: true),
                    last_status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    terminal_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_jobs", x => x.id);
                    table.CheckConstraint("ck_inbox_jobs_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_inbox_jobs_created_has_parcel", "state <> 'Created' OR (parcel_id IS NOT NULL AND tracking_number IS NOT NULL AND parcel_status IS NOT NULL AND parcel_version IS NOT NULL)");
                    table.CheckConstraint("ck_inbox_jobs_external_order_id", "btrim(external_order_id) <> ''");
                    table.CheckConstraint("ck_inbox_jobs_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_inbox_jobs_idempotency_key", "btrim(idempotency_key) <> ''");
                    table.CheckConstraint("ck_inbox_jobs_parcel_version", "parcel_version IS NULL OR parcel_version >= 1");
                    table.CheckConstraint("ck_inbox_jobs_state", "state IN ('Accepted','Retrying','Created','Failed')");
                    table.CheckConstraint("ck_inbox_jobs_total", "total IS NULL OR total >= 0");
                    table.CheckConstraint("ck_inbox_jobs_unsettled_is_scheduled", "state NOT IN ('Accepted','Retrying') OR next_retry_at IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_inbox_jobs_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inbox_jobs_business_id_created_at_id",
                table: "inbox_jobs",
                columns: new[] { "business_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_inbox_jobs_business_id_external_order_id",
                table: "inbox_jobs",
                columns: new[] { "business_id", "external_order_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inbox_jobs_state_next_retry_at",
                table: "inbox_jobs",
                columns: new[] { "state", "next_retry_at" });

            // Orders are lifetime records: the runtime role can read, insert and update, never delete.
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON TABLE public.inbox_jobs TO merchant_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE ON TABLE public.inbox_jobs FROM merchant_app;");

            migrationBuilder.DropTable(
                name: "inbox_jobs");
        }
    }
}
