using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProofObjectsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // proof_objects stores courier proof images (photos/signatures) as bytea.
            // This table is managed by raw SQL (PostgresProofStorage) and is intentionally
            // outside the EF entity model to avoid loading binary blobs via change tracking.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS proof_objects (
                    storage_ref  text        PRIMARY KEY,
                    content_type text        NOT NULL,
                    sha256       text        NOT NULL,
                    length       bigint      NOT NULL,
                    data         bytea       NOT NULL,
                    stored_at    timestamptz NOT NULL DEFAULT now()
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS proof_objects;");
        }
    }
}

