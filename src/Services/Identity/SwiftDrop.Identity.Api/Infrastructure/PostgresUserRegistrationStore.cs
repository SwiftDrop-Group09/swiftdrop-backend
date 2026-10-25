using SwiftDrop.Identity.Api.Application;

using Npgsql;

namespace SwiftDrop.Identity.Api.Infrastructure;

public sealed class PostgresUserRegistrationStore(NpgsqlDataSource dataSource) : IUserRegistrationStore
{
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('public.schema_migrations') IS NOT NULL",
            connection);
        var migrationTableExists = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        if (!migrationTableExists)
        {
            return false;
        }

        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM public.schema_migrations WHERE version = @version)";
        command.Parameters.AddWithValue("version", IdentitySchemaMigrator.CurrentVersion);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<RegisteredProfile?> RegisterAsync(
        string email,
        string normalizedEmail,
        string fullName,
        string passwordHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var id = Guid.NewGuid();
            await using (var insertUser = new NpgsqlCommand(
                """
                INSERT INTO public.users
                    (id, email, normalized_email, full_name, password_hash, is_active)
                VALUES
                    (@id, @email, @normalized_email, @full_name, @password_hash, TRUE)
                """,
                connection,
                transaction))
            {
                insertUser.Parameters.AddWithValue("id", id);
                insertUser.Parameters.AddWithValue("email", email);
                insertUser.Parameters.AddWithValue("normalized_email", normalizedEmail);
                insertUser.Parameters.AddWithValue("full_name", fullName);
                insertUser.Parameters.AddWithValue("password_hash", passwordHash);
                await insertUser.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var assignCustomer = new NpgsqlCommand(
                """
                INSERT INTO public.user_roles (user_id, role_name)
                VALUES (@user_id, 'Customer')
                """,
                connection,
                transaction))
            {
                assignCustomer.Parameters.AddWithValue("user_id", id);
                await assignCustomer.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insertAudit = new NpgsqlCommand(
                """
                INSERT INTO public.audit_logs (actor_user_id, action, subject_user_id)
                VALUES (@user_id, 'UserRegistered', @user_id)
                """,
                connection,
                transaction))
            {
                insertAudit.Parameters.AddWithValue("user_id", id);
                await insertAudit.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new RegisteredProfile(id, email, fullName, null, null, ["Customer"], true);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.UniqueViolation
                && exception.ConstraintName == "users_normalized_email_key")
        {
            return null;
        }
    }
}
