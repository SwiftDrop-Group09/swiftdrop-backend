using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SwiftDrop.Auditing;

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DeliveryDbContext))]
[Migration("20261113010000_ActorActivityAudit")]
public sealed class ActorActivityAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ActivityAuditSchema.Sql + ActivityAuditSchema.GrantFor("delivery_app"));
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(ActivityAuditSchema.DownSql);
}
