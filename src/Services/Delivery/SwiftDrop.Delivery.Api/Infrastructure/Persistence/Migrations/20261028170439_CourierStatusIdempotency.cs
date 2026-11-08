using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CourierStatusIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_completion",
                table: "idempotency_keys");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_completion",
                table: "idempotency_keys",
                sql: "((parcel_id IS NULL AND stored_response IS NULL) OR (parcel_id IS NOT NULL AND stored_response IS NOT NULL AND jsonb_typeof(stored_response) = 'object' AND stored_response ?& ARRAY['statusCode','body','location'] AND (stored_response->'statusCode' = '201'::jsonb OR (operation = 'UpdateDeliveryStatus' AND stored_response->'statusCode' = '200'::jsonb)) AND jsonb_typeof(stored_response->'body') = 'string' AND jsonb_typeof(stored_response->'location') = 'string')) IS TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_completion",
                table: "idempotency_keys");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_completion",
                table: "idempotency_keys",
                sql: "((parcel_id IS NULL AND stored_response IS NULL) OR (parcel_id IS NOT NULL AND stored_response IS NOT NULL AND jsonb_typeof(stored_response) = 'object' AND stored_response ?& ARRAY['statusCode','body','location'] AND stored_response->'statusCode' = '201'::jsonb AND jsonb_typeof(stored_response->'body') = 'string' AND jsonb_typeof(stored_response->'location') = 'string')) IS TRUE");
        }
    }
}
