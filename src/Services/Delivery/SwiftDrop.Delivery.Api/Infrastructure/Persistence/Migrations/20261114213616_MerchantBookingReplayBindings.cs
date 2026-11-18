using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MerchantBookingReplayBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "merchant_booking_identities",
                columns: table => new
                {
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_order_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    parcel_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merchant_booking_identities", x => new { x.merchant_id, x.external_order_id });
                    table.ForeignKey(
                        name: "FK_merchant_booking_identities_parcels_parcel_id",
                        column: x => x.parcel_id,
                        principalTable: "parcels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_merchant_booking_identities_merchant_id_idempotency_key",
                table: "merchant_booking_identities",
                columns: new[] { "merchant_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_merchant_booking_identities_parcel_id",
                table: "merchant_booking_identities",
                column: "parcel_id");
            migrationBuilder.Sql("""
                DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'delivery_app') THEN
                    GRANT SELECT, INSERT ON TABLE merchant_booking_identities TO delivery_app;
                END IF; END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "merchant_booking_identities");
        }
    }
}
