using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftDrop.Merchant.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhook_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    subscribed_events = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    secret_ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    secret_nonce = table.Column<byte[]>(type: "bytea", maxLength: 12, nullable: false),
                    secret_tag = table.Column<byte[]>(type: "bytea", maxLength: 16, nullable: false),
                    key_version = table.Column<int>(type: "integer", nullable: false),
                    secret_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_subscriptions", x => x.id);
                    table.CheckConstraint("ck_webhook_subscriptions_endpoint", "btrim(endpoint_url) <> ''");
                    table.CheckConstraint("ck_webhook_subscriptions_key_version", "key_version > 0");
                    table.CheckConstraint("ck_webhook_subscriptions_secret_version", "secret_version > 0");
                    table.ForeignKey(
                        name: "FK_webhook_subscriptions_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_dispatch_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    replay_generation = table.Column<int>(type: "integer", nullable: false),
                    frozen_event_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    secret_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    leased_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    safe_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_dispatch_jobs", x => x.id);
                    table.CheckConstraint("ck_webhook_dispatch_jobs_attempts", "attempt_count >= 0");
                    table.CheckConstraint("ck_webhook_dispatch_jobs_replay_gen", "replay_generation >= 0");
                    table.CheckConstraint("ck_webhook_dispatch_jobs_status", "status IN ('Pending','PendingResolution','Processing','Delivered','Failed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_webhook_dispatch_jobs_webhook_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "webhook_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_replay_audits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    replay_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_replay_audits", x => x.id);
                    table.CheckConstraint("ck_webhook_replay_audits_reason", "btrim(reason) <> ''");
                    table.ForeignKey(
                        name: "FK_webhook_replay_audits_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_webhook_replay_audits_webhook_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "webhook_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    response_status_code = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    next_retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_attempts", x => x.id);
                    table.CheckConstraint("ck_webhook_attempts_duration", "duration_ms >= 0");
                    table.CheckConstraint("ck_webhook_attempts_number", "attempt_number > 0");
                    table.ForeignKey(
                        name: "FK_webhook_attempts_webhook_dispatch_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "webhook_dispatch_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_attempts_job_id",
                table: "webhook_attempts",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_attempts_job_id_attempt_number",
                table: "webhook_attempts",
                columns: new[] { "job_id", "attempt_number" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_dispatch_jobs_event_id_subscription_id_replay_gener~",
                table: "webhook_dispatch_jobs",
                columns: new[] { "event_id", "subscription_id", "replay_generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_dispatch_jobs_status_due_at",
                table: "webhook_dispatch_jobs",
                columns: new[] { "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_dispatch_jobs_subscription_id",
                table: "webhook_dispatch_jobs",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_replay_audits_business_id",
                table: "webhook_replay_audits",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_replay_audits_original_job_id",
                table: "webhook_replay_audits",
                column: "original_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_replay_audits_subscription_id",
                table: "webhook_replay_audits",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscriptions_business_id",
                table: "webhook_subscriptions",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscriptions_business_id_is_active",
                table: "webhook_subscriptions",
                columns: new[] { "business_id", "is_active" });

            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.webhook_subscriptions, public.webhook_dispatch_jobs, public.webhook_attempts, public.webhook_replay_audits TO merchant_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLE public.webhook_subscriptions, public.webhook_dispatch_jobs, public.webhook_attempts, public.webhook_replay_audits FROM merchant_app;");

            migrationBuilder.DropTable(
                name: "webhook_attempts");

            migrationBuilder.DropTable(
                name: "webhook_replay_audits");

            migrationBuilder.DropTable(
                name: "webhook_dispatch_jobs");

            migrationBuilder.DropTable(
                name: "webhook_subscriptions");
        }
    }
}
