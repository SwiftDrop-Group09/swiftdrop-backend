using System.ComponentModel.DataAnnotations;

using Npgsql;

namespace SwiftDrop.Identity.Api.Infrastructure.Authentication;

/// <summary>
/// Explicit offline provisioning for a trusted local developer. This operation is
/// deliberately unavailable through HTTP and cannot promote or reset any account.
/// </summary>
public static class DevelopmentAdminProvisioner
{
    public static async Task ProvisionAsync(
        IConfiguration configuration, IHostEnvironment environment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Development Admin accounts can only be provisioned in Development.");
        }

        var email = configuration["Identity:DevelopmentAdmin:Email"]?.Trim();
        var fullName = configuration["Identity:DevelopmentAdmin:FullName"]?.Trim();
        var password = configuration["Identity:DevelopmentAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || !new EmailAddressAttribute().IsValid(email)
            || string.IsNullOrWhiteSpace(fullName) || fullName.Length > 120 || password is null || password.Length is < 12 or > 128)
        {
            throw new InvalidOperationException("Valid external Development Admin email, full name and a 12–128 character password are required.");
        }

        var connectionString = configuration.GetConnectionString("Database");
        NpgsqlConnectionStringBuilder settings;
        try
        {
            settings = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("Valid Identity application database access is required for provisioning.");
        }

        if (settings.Database != "swiftdrop_identity")
        {
            throw new InvalidOperationException("Development Admin provisioning requires the swiftdrop_identity database.");
        }

        try
        {
            await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var administrationLock = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(7342003,1)", connection, transaction))
            {
                await administrationLock.ExecuteNonQueryAsync(cancellationToken);
            }

            await RequireCurrentMigrationAsync(connection, transaction, cancellationToken);
            var normalizedEmail = email.ToUpperInvariant();
            await using (var existing = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM public.users WHERE normalized_email=@email)", connection, transaction))
            {
                existing.Parameters.AddWithValue("email", normalizedEmail);
                if ((bool)(await existing.ExecuteScalarAsync(cancellationToken))!)
                {
                    throw new InvalidOperationException("Development Admin provisioning refuses existing accounts.");
                }
            }

            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var passwordHash = await new Argon2PasswordHasher().HashAsync(password, cancellationToken);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO public.users
                    (id,email,normalized_email,full_name,password_hash,is_active,created_at,updated_at)
                VALUES (@id,@email,@normalized_email,@full_name,@password_hash,TRUE,@now,@now);
                INSERT INTO public.user_roles (user_id,role_name,assigned_at)
                VALUES (@id,'Customer',@now),(@id,'Admin',@now);
                INSERT INTO public.audit_logs
                    (actor_user_id,subject_user_id,action,created_at,result_code,correlation_id)
                VALUES (NULL,@id,'UserCreated',@now,'Success',@correlation);
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("email", email);
            insert.Parameters.AddWithValue("normalized_email", normalizedEmail);
            insert.Parameters.AddWithValue("full_name", fullName);
            insert.Parameters.AddWithValue("password_hash", passwordHash);
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("correlation", Guid.NewGuid().ToString("N"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or ArgumentException)
        {
            // Keep command-line failures free of connection settings or SQL details.
            throw new InvalidOperationException("Development Admin provisioning could not be completed.");
        }
    }

    private static async Task RequireCurrentMigrationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var ready = new NpgsqlCommand(
            "SELECT to_regclass('public.schema_migrations') IS NOT NULL", connection, transaction);
        if (!(bool)(await ready.ExecuteScalarAsync(cancellationToken))!)
        {
            throw new InvalidOperationException("Apply the current Identity migrations before provisioning.");
        }

        ready.CommandText = "SELECT EXISTS (SELECT 1 FROM public.schema_migrations WHERE version=@version)";
        ready.Parameters.AddWithValue("version", IdentitySchemaMigrator.CurrentVersion);
        if (!(bool)(await ready.ExecuteScalarAsync(cancellationToken))!)
        {
            throw new InvalidOperationException("Apply the current Identity migrations before provisioning.");
        }
    }
}
