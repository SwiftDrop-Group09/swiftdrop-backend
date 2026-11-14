using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryConfigAuditPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "delivery_zones",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateTable(
                name: "delivery_config_audits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    resource_type = table.Column<string>(type: "text", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    details = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_config_audits", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_pricing_active_zone_size",
                table: "pricing_tiers",
                columns: new[] { "zone_id", "size_category" },
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_config_audits_resource_type_resource_id",
                table: "delivery_config_audits",
                columns: new[] { "resource_type", "resource_id" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_config_audits_timestamp",
                table: "delivery_config_audits",
                column: "timestamp");
            migrationBuilder.Sql("""
                CREATE FUNCTION delivery_guard_config_audit() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Delivery configuration audit records are append-only'
                        USING ERRCODE = '23514', CONSTRAINT = 'ck_delivery_config_audit_retained';
                END;
                $$;
                CREATE TRIGGER delivery_config_audit_guard
                    BEFORE UPDATE OR DELETE ON delivery_config_audits
                    FOR EACH ROW EXECUTE FUNCTION delivery_guard_config_audit();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER delivery_config_audit_guard ON delivery_config_audits; DROP FUNCTION delivery_guard_config_audit();");
            migrationBuilder.DropTable(
                name: "delivery_config_audits");

            migrationBuilder.DropIndex(
                name: "ux_pricing_active_zone_size",
                table: "pricing_tiers");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "delivery_zones");
        }
    }
}
