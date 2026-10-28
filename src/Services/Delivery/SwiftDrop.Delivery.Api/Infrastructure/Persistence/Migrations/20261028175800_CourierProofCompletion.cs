using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CourierProofCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION delivery_guard_proof() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Committed proof metadata is retained and immutable'
                        USING ERRCODE = '23514', CONSTRAINT = 'ck_proof_metadata_retained';
                END;
                $$;
                CREATE TRIGGER proof_metadata_retained
                    BEFORE UPDATE OR DELETE ON proof_metadata
                    FOR EACH ROW EXECUTE FUNCTION delivery_guard_proof();
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_completion",
                table: "idempotency_keys");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_completion",
                table: "idempotency_keys",
                sql: "((parcel_id IS NULL AND stored_response IS NULL) OR (parcel_id IS NOT NULL AND stored_response IS NOT NULL AND jsonb_typeof(stored_response) = 'object' AND stored_response ?& ARRAY['statusCode','body','location'] AND (stored_response->'statusCode' = '201'::jsonb OR (operation IN ('UpdateDeliveryStatus','CompleteDeliveryWithProof') AND stored_response->'statusCode' = '200'::jsonb)) AND jsonb_typeof(stored_response->'body') = 'string' AND jsonb_typeof(stored_response->'location') = 'string')) IS TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER proof_metadata_retained ON proof_metadata;
                DROP FUNCTION delivery_guard_proof();
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_completion",
                table: "idempotency_keys");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_completion",
                table: "idempotency_keys",
                sql: "((parcel_id IS NULL AND stored_response IS NULL) OR (parcel_id IS NOT NULL AND stored_response IS NOT NULL AND jsonb_typeof(stored_response) = 'object' AND stored_response ?& ARRAY['statusCode','body','location'] AND (stored_response->'statusCode' = '201'::jsonb OR (operation = 'UpdateDeliveryStatus' AND stored_response->'statusCode' = '200'::jsonb)) AND jsonb_typeof(stored_response->'body') = 'string' AND jsonb_typeof(stored_response->'location') = 'string')) IS TRUE");
        }
    }
}
