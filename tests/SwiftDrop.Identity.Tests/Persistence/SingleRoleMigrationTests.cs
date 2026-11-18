using Npgsql;
using SwiftDrop.Contracts;
using SwiftDrop.Identity.Api.Infrastructure;

namespace SwiftDrop.Identity.Tests.Persistence;

public sealed class SingleRoleMigrationTests
{
    [Fact]
    public async Task ConsolidationArchivesAssignmentsPreservesAccountsAndEnforcesExactlyOneEnumRole()
    {
        var fixture = new IdentityPostgresFixture();
        try
        {
            await fixture.InitializeAsync(false);
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE public.schema_migrations(version text PRIMARY KEY,applied_at timestamptz NOT NULL DEFAULT NOW())";
            await command.ExecuteNonQueryAsync();
            foreach (var version in new[] { "001_identity_initial", "002_identity_sessions", "003_identity_administration", "004_actor_activity_audit" })
            {
                await using var stream = typeof(IdentitySchemaMigrator).Assembly.GetManifestResourceStream($"SwiftDrop.Identity.Api.Infrastructure.Migrations.{version}.sql")!;
                using var reader = new StreamReader(stream);
                command.CommandText = await reader.ReadToEndAsync();
                await command.ExecuteNonQueryAsync();
                command.CommandText = "INSERT INTO public.schema_migrations(version) VALUES (@version)";
                command.Parameters.AddWithValue("version", version);
                await command.ExecuteNonQueryAsync(); command.Parameters.Clear();
            }
            (string[] Before, string After)[] cases = [([], "Customer"), (["Customer","Admin"],"Admin"),
                (["Courier","Merchant"],"Merchant"), (["Merchant","Dispatcher"],"Dispatcher"),
                (["Customer","Courier"],"Courier"), (["Customer"],"Customer")];
            var ids = cases.Select(_ => Guid.NewGuid()).ToArray();
            for (var i=0;i<cases.Length;i++)
            {
                command.CommandText = """
                    INSERT INTO public.users(id,email,normalized_email,full_name,password_hash)
                    VALUES (@id,@email,@email,'Preserved account','preserved-credential-hash');
                    INSERT INTO public.user_roles(user_id,role_name) SELECT @id,role FROM unnest(@roles::text[]) role;
                    """;
                command.Parameters.AddWithValue("id",ids[i]); command.Parameters.AddWithValue("email",$"{i}@example.test");
                command.Parameters.AddWithValue("roles",cases[i].Before);
                await command.ExecuteNonQueryAsync(); command.Parameters.Clear();
            }
            await IdentitySchemaMigrator.ApplyAsync(fixture.ConnectionString);
            for (var i=0;i<cases.Length;i++)
            {
                command.CommandText = "SELECT role_name::text FROM public.user_roles WHERE user_id=@id";
                command.Parameters.AddWithValue("id",ids[i]);
                Assert.Equal(cases[i].After,await command.ExecuteScalarAsync()); command.Parameters.Clear();
            }
            command.CommandText = "SELECT count(*) FROM public.users WHERE password_hash='preserved-credential-hash'";
            Assert.Equal(6L,await command.ExecuteScalarAsync());
            command.CommandText = "SELECT count(*) FROM public.user_role_migration_archive";
            Assert.Equal(10L,await command.ExecuteScalarAsync());
            command.CommandText = "SELECT pg_typeof(role_name)::text FROM public.user_roles LIMIT 1";
            Assert.Equal("user_role",await command.ExecuteScalarAsync());
            command.CommandText = "INSERT INTO public.user_roles(user_id,role_name) VALUES (@id,'Courier')";
            command.Parameters.AddWithValue("id",ids[0]);
            Assert.Equal(PostgresErrorCodes.UniqueViolation,(await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            command.CommandText = "DELETE FROM public.user_roles WHERE user_id=@id";
            Assert.Equal(PostgresErrorCodes.CheckViolation,(await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            command.CommandText = "UPDATE public.user_roles SET role_name='UndefinedRole' WHERE user_id=@id";
            Assert.Equal(PostgresErrorCodes.InvalidTextRepresentation,(await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            command.Parameters.Clear();
            await IdentitySchemaMigrator.ApplyAsync(fixture.ConnectionString);
            command.CommandText = "SELECT count(*) FROM public.user_role_migration_archive";
            Assert.Equal(10L,await command.ExecuteScalarAsync());
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Theory]
    [InlineData("Admin", true)] [InlineData("Customer", true)] [InlineData("admin",false)]
    [InlineData("0",false)] [InlineData("Undefined",false)]
    public void HumanRoleContractUsesOnlyNamedEnumValues(string value, bool valid) => Assert.Equal(valid,HumanRoles.TryParse(value,out _));
}
