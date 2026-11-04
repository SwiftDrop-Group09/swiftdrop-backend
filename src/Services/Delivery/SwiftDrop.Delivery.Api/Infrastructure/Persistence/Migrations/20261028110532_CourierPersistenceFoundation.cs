using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CourierPersistenceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "couriers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    on_duty = table.Column<bool>(type: "boolean", nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_couriers", x => x.id);
                    table.CheckConstraint("ck_couriers_capacity", "capacity >= 1");
                    table.CheckConstraint("ck_couriers_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_couriers_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "proof_metadata",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parcel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    storage_ref = table.Column<string>(type: "text", nullable: false),
                    recipient_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proof_metadata", x => x.id);
                    table.CheckConstraint("ck_proof_actor", "uploaded_by <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_proof_recipient", "btrim(recipient_name) <> ''");
                    table.CheckConstraint("ck_proof_storage_ref", "btrim(storage_ref) <> '' AND storage_ref !~ '^[A-Za-z][A-Za-z0-9+.-]*:' AND storage_ref !~ '^//'");
                    table.CheckConstraint("ck_proof_type", "type IN ('Photo','Signature')");
                    table.ForeignKey(
                        name: "FK_proof_metadata_parcels_parcel_id",
                        column: x => x.parcel_id,
                        principalTable: "parcels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "courier_zones",
                columns: table => new
                {
                    courier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    zone_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_courier_zones", x => new { x.courier_id, x.zone_id });
                    table.ForeignKey(
                        name: "FK_courier_zones_couriers_courier_id",
                        column: x => x.courier_id,
                        principalTable: "couriers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_courier_zones_delivery_zones_zone_id",
                        column: x => x.zone_id,
                        principalTable: "delivery_zones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_courier_zones_zone_id",
                table: "courier_zones",
                column: "zone_id");

            migrationBuilder.CreateIndex(
                name: "IX_proof_metadata_parcel_id_uploaded_at_id",
                table: "proof_metadata",
                columns: new[] { "parcel_id", "uploaded_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_proof_metadata_storage_ref",
                table: "proof_metadata",
                column: "storage_ref",
                unique: true);

            // Preserve every historical actor/state/note even when EF is bypassed.
            // Use the migration connection's search path, as in the booking migration.
            migrationBuilder.Sql("""
                CREATE FUNCTION delivery_guard_history() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Parcel status history is append-only'
                        USING ERRCODE = '23514', CONSTRAINT = 'ck_booking_history_append_only';
                END;
                $$;
                CREATE TRIGGER booking_history_append_only
                    BEFORE UPDATE OR DELETE ON booking_history
                    FOR EACH ROW EXECUTE FUNCTION delivery_guard_history();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER booking_history_append_only ON booking_history;
                DROP FUNCTION delivery_guard_history();
                """);

            migrationBuilder.DropTable(
                name: "courier_zones");

            migrationBuilder.DropTable(
                name: "proof_metadata");

            migrationBuilder.DropTable(
                name: "couriers");
        }
    }
}
