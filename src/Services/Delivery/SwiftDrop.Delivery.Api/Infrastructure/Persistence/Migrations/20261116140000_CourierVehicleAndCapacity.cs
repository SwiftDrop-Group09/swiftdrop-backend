using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(DeliveryDbContext))]
    [Migration("20261116140000_CourierVehicleAndCapacity")]
    public partial class CourierVehicleAndCapacity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "vehicle_type",
                table: "couriers",
                type: "text",
                nullable: false,
                defaultValue: "Motorcycle");

            migrationBuilder.AddColumn<string>(
                name: "vehicle_number",
                table: "couriers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "max_weight_kg",
                table: "couriers",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: false,
                defaultValue: 30.00m);

            migrationBuilder.AddCheckConstraint(
                name: "ck_couriers_max_weight",
                table: "couriers",
                sql: "max_weight_kg > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_couriers_vehicle_type",
                table: "couriers",
                sql: "vehicle_type IN ('Bicycle','Motorcycle','Car','Van','Truck')");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_couriers_vehicle_type",
                table: "couriers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_couriers_max_weight",
                table: "couriers");

            migrationBuilder.DropColumn(
                name: "max_weight_kg",
                table: "couriers");

            migrationBuilder.DropColumn(
                name: "vehicle_number",
                table: "couriers");

            migrationBuilder.DropColumn(
                name: "vehicle_type",
                table: "couriers");
        }
    }
}

