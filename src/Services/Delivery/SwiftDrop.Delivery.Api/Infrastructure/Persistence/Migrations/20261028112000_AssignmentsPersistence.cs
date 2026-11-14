using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssignmentsPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parcel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    courier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    picked_up_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignments", x => x.id);
                    table.CheckConstraint("ck_assignments_delivery", "(status = 'Delivered' AND delivered_at IS NOT NULL AND picked_up_at IS NOT NULL) OR (status <> 'Delivered' AND delivered_at IS NULL)");
                    table.CheckConstraint("ck_assignments_release", "status NOT IN ('Reassigned','Cancelled') OR picked_up_at IS NULL");
                    table.CheckConstraint("ck_assignments_status", "status IN ('Active','Reassigned','Delivered','Failed','Cancelled')");
                    table.CheckConstraint("ck_assignments_timestamps", "(picked_up_at IS NULL OR picked_up_at >= assigned_at) AND (delivered_at IS NULL OR (picked_up_at IS NOT NULL AND delivered_at >= picked_up_at))");
                    table.ForeignKey(
                        name: "FK_assignments_couriers_courier_id",
                        column: x => x.courier_id,
                        principalTable: "couriers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assignments_parcels_parcel_id",
                        column: x => x.parcel_id,
                        principalTable: "parcels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assignments_courier_id_service_date_assigned_at_id",
                table: "assignments",
                columns: new[] { "courier_id", "service_date", "assigned_at", "id" },
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_assignments_courier_id_status_assigned_at_id",
                table: "assignments",
                columns: new[] { "courier_id", "status", "assigned_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_assignments_current_parcel",
                table: "assignments",
                column: "parcel_id",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.Sql("""
                CREATE FUNCTION delivery_guard_assignment() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Assignment history must be retained'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_assignments_retained';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status <> 'Active' OR NEW.picked_up_at IS NOT NULL OR NEW.delivered_at IS NOT NULL THEN
                            RAISE EXCEPTION 'New assignments start active before pickup'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_assignments_initial';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF (to_jsonb(NEW) - ARRAY['status','picked_up_at','delivered_at']) IS DISTINCT FROM
                       (to_jsonb(OLD) - ARRAY['status','picked_up_at','delivered_at']) THEN
                        RAISE EXCEPTION 'Assignment identity, ownership and schedule are immutable'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_assignments_ownership_immutable';
                    END IF;
                    IF OLD.status <> 'Active' THEN
                        RAISE EXCEPTION 'A closed assignment cannot be changed'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_assignments_closed_immutable';
                    END IF;
                    IF OLD.picked_up_at IS NOT NULL AND NEW.picked_up_at IS DISTINCT FROM OLD.picked_up_at THEN
                        RAISE EXCEPTION 'Recorded pickup time is immutable'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_assignments_pickup_immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER assignments_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON assignments
                    FOR EACH ROW EXECUTE FUNCTION delivery_guard_assignment();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER assignments_guard ON assignments;
                DROP FUNCTION delivery_guard_assignment();
                """);

            migrationBuilder.DropTable(
                name: "assignments");
        }
    }
}
