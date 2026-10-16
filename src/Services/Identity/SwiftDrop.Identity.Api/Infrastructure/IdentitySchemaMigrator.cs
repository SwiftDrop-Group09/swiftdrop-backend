using System.Reflection;
using Npgsql;

namespace SwiftDrop.Identity.Api.Infrastructure;

public static class IdentitySchemaMigrator
{
    private const string MigrationVersion = "001_identity_initial";
    private const string MigrationResource =
        "SwiftDrop.Identity.Api.Infrastructure.Migrations.001_identity_initial.sql";

    public static async Task ApplyAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(7342001, 1)",
            connection,
            transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var versionTable = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS public.schema_migrations (
                version TEXT PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            )
            """,
            connection,
            transaction))
        {
            await versionTable.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var versionCheck = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM public.schema_migrations WHERE version = @version)",
            connection,
            transaction))
        {
            versionCheck.Parameters.AddWithValue("version", MigrationVersion);
            if ((bool)(await versionCheck.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        await using var migrationStream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(MigrationResource)
            ?? throw new InvalidOperationException($"Embedded migration {MigrationVersion} was not found.");
        using var reader = new StreamReader(migrationStream);
        var migrationSql = await reader.ReadToEndAsync(cancellationToken);

        await using (var migration = new NpgsqlCommand(migrationSql, connection, transaction))
        {
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var recordMigration = new NpgsqlCommand(
            "INSERT INTO public.schema_migrations (version) VALUES (@version)",
            connection,
            transaction))
        {
            recordMigration.Parameters.AddWithValue("version", MigrationVersion);
            await recordMigration.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
