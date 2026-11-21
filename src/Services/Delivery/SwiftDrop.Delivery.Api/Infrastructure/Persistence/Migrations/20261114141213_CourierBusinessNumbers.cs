using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CourierBusinessNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "business_number",
                table: "couriers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn);

            migrationBuilder.CreateIndex(
                name: "IX_couriers_business_number",
                table: "couriers",
                column: "business_number",
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION delivery_guard_courier_number() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.business_number IS DISTINCT FROM OLD.business_number THEN
                        RAISE EXCEPTION 'Courier business numbers are immutable' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER courier_number_guard BEFORE UPDATE ON couriers
                    FOR EACH ROW EXECUTE FUNCTION delivery_guard_courier_number();
                DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='delivery_app') THEN
                    GRANT USAGE, SELECT ON SEQUENCE couriers_business_number_seq TO delivery_app;
                END IF; END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN IF EXISTS (SELECT 1 FROM couriers) THEN
                    RAISE EXCEPTION 'Retained courier business numbers cannot be discarded' USING ERRCODE='23514';
                END IF; END $$;
                """);
            migrationBuilder.Sql("DROP TRIGGER courier_number_guard ON couriers; DROP FUNCTION delivery_guard_courier_number();");
            migrationBuilder.DropIndex(
                name: "IX_couriers_business_number",
                table: "couriers");

            migrationBuilder.DropColumn(
                name: "business_number",
                table: "couriers");
        }
    }
}
