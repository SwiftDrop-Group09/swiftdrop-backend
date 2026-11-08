using System.Data;

using Npgsql;
using NpgsqlTypes;

using SwiftDrop.Identity.Api.Application;

namespace SwiftDrop.Identity.Api.Infrastructure;

public sealed class PostgresIdentityAdministrationStore(NpgsqlDataSource dataSource) : IIdentityAdministrationStore
{
    private const string ProfileColumns = """
        u.id, u.email, u.full_name, u.phone_number, u.address, u.is_active,
        ARRAY(SELECT ur.role_name::text FROM public.user_roles ur
              WHERE ur.user_id = u.id ORDER BY ur.role_name)
        """;

    public async Task<IdentityAdministrationResult<RegisteredProfile>> GetProfileAsync(
        Guid actor, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var profile = await ReadProfileAsync(connection, null, actor, cancellationToken);
        return profile is { IsActive: true }
            ? new(profile)
            : Failure<RegisteredProfile>(IdentityAdministrationFailure.Forbidden);
    }

    public async Task<IdentityAdministrationResult<RegisteredProfile>> UpdateProfileAsync(
        Guid actor, ProfileUpdate input, DateTimeOffset now, string? correlation, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var profiles = await LockUsersAsync(connection, transaction, [actor], cancellationToken);
        if (!profiles.TryGetValue(actor, out var profile) || !profile.IsActive)
        {
            return Failure<RegisteredProfile>(IdentityAdministrationFailure.Forbidden);
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE public.users SET full_name=@full_name, phone_number=@phone,
                address=@address, updated_at=@now WHERE id=@id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", actor);
            update.Parameters.AddWithValue("full_name", input.FullName);
            update.Parameters.Add("phone", NpgsqlDbType.Varchar).Value = (object?)input.PhoneNumber ?? DBNull.Value;
            update.Parameters.Add("address", NpgsqlDbType.Text).Value = (object?)input.Address ?? DBNull.Value;
            update.Parameters.AddWithValue("now", now);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await AuditAsync(connection, transaction, actor, actor, "ProfileUpdated", now, correlation, cancellationToken);
        var updated = await ReadProfileAsync(connection, transaction, actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(updated);
    }

    public async Task<IdentityAdministrationResult<IdentityPage<RegisteredProfile>>> ListUsersAsync(
        Guid actor, int page, int pageSize, CancellationToken cancellationToken)
    {
        ValidatePage(page, pageSize);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        if (!IsAdministrator(await ReadProfileAsync(connection, transaction, actor, cancellationToken)))
        {
            return Failure<IdentityPage<RegisteredProfile>>(IdentityAdministrationFailure.Forbidden);
        }

        await using var count = new NpgsqlCommand("SELECT COUNT(*) FROM public.users", connection, transaction);
        var total = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        await using var list = new NpgsqlCommand($"""
            SELECT {ProfileColumns} FROM public.users u
            ORDER BY u.created_at, u.id LIMIT @limit OFFSET @offset
            """, connection, transaction);
        AddPage(list, page, pageSize);
        var items = new List<RegisteredProfile>();
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) items.Add(ReadProfile(reader));
        }

        await transaction.CommitAsync(cancellationToken);
        return new(new IdentityPage<RegisteredProfile>(items, total, page, pageSize));
    }

    public async Task<IdentityAdministrationResult<RegisteredProfile>> CreateUserAsync(
        Guid actor, string email, string normalizedEmail, string fullName, bool isActive, string passwordHash,
        DateTimeOffset now, string? correlation, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAdministrationAsync(connection, transaction, cancellationToken);
        var profiles = await LockUsersAsync(connection, transaction, [actor], cancellationToken);
        if (!profiles.TryGetValue(actor, out var administrator) || !IsAdministrator(administrator))
        {
            return Failure<RegisteredProfile>(IdentityAdministrationFailure.Forbidden);
        }

        try
        {
            var target = Guid.NewGuid();
            await using (var create = new NpgsqlCommand("""
                INSERT INTO public.users
                    (id,email,normalized_email,full_name,is_active,password_hash,created_at,updated_at)
                VALUES (@id,@email,@normalized_email,@full_name,@active,@password_hash,@now,@now);
                INSERT INTO public.user_roles (user_id,role_name,assigned_at) VALUES (@id,'Customer',@now);
                """, connection, transaction))
            {
                AddUserParameters(create, target, email, normalizedEmail, fullName, isActive, now);
                create.Parameters.AddWithValue("password_hash", passwordHash);
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await AuditAsync(connection, transaction, actor, target, "UserCreated", now, correlation, cancellationToken);
            var created = await ReadProfileAsync(connection, transaction, target, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(created);
        }
        catch (PostgresException exception) when (IsEmailConflict(exception))
        {
            return Failure<RegisteredProfile>(IdentityAdministrationFailure.Conflict);
        }
    }

    public async Task<IdentityAdministrationResult<RegisteredProfile>> UpdateUserAsync(
        Guid actor, Guid target, string email, string normalizedEmail, string fullName, bool isActive,
        DateTimeOffset now, string? correlation, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAdministrationAsync(connection, transaction, cancellationToken);
        var profiles = await LockUsersAsync(connection, transaction, [actor, target], cancellationToken);
        var failure = CheckAdministratorAndTarget(profiles, actor, target);
        if (failure is not null) return Failure<RegisteredProfile>(failure.Value);
        var previous = profiles[target];
        if (!isActive && await IsLastActiveAdministratorAsync(connection, transaction, previous, cancellationToken))
        {
            return Failure<RegisteredProfile>(IdentityAdministrationFailure.LastAdmin);
        }

        try
        {
            await using (var update = new NpgsqlCommand("""
                UPDATE public.users SET email=@email, normalized_email=@normalized_email,
                    full_name=@full_name,is_active=@active,updated_at=@now WHERE id=@id
                """, connection, transaction))
            {
                AddUserParameters(update, target, email, normalizedEmail, fullName, isActive, now);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            if (!isActive)
            {
                await RevokeUserSessionsAsync(connection, transaction, target, now, cancellationToken);
            }

            await AuditAsync(connection, transaction, actor, target, "UserUpdated", now, correlation, cancellationToken);
            var updated = await ReadProfileAsync(connection, transaction, target, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(updated);
        }
        catch (PostgresException exception) when (IsEmailConflict(exception))
        {
            return Failure<RegisteredProfile>(IdentityAdministrationFailure.Conflict);
        }
    }

    public async Task<IdentityAdministrationResult<RegisteredProfile>> SetRolesAsync(
        Guid actor, Guid target, string[] roles, DateTimeOffset now, string? correlation, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAdministrationAsync(connection, transaction, cancellationToken);
        var profiles = await LockUsersAsync(connection, transaction, [actor, target], cancellationToken);
        var failure = CheckAdministratorAndTarget(profiles, actor, target);
        if (failure is not null) return Failure<RegisteredProfile>(failure.Value);

        if (roles.Length > 5 || roles.Any(string.IsNullOrWhiteSpace)
            || roles.Distinct(StringComparer.Ordinal).Count() != roles.Length)
        {
            return Failure<RegisteredProfile>(IdentityAdministrationFailure.InvalidRoles);
        }

        await using (var knownRoles = new NpgsqlCommand(
            "SELECT COUNT(*) FROM public.roles WHERE name=ANY(@roles)", connection, transaction))
        {
            knownRoles.Parameters.AddWithValue("roles", roles);
            if ((long)(await knownRoles.ExecuteScalarAsync(cancellationToken))! != roles.Length)
            {
                return Failure<RegisteredProfile>(IdentityAdministrationFailure.InvalidRoles);
            }
        }

        if (!roles.Contains("Admin", StringComparer.Ordinal)
            && await IsLastActiveAdministratorAsync(connection, transaction, profiles[target], cancellationToken))
        {
            return Failure<RegisteredProfile>(IdentityAdministrationFailure.LastAdmin);
        }

        await using (var replace = new NpgsqlCommand("""
            DELETE FROM public.user_roles WHERE user_id=@target;
            INSERT INTO public.user_roles (user_id,role_name,assigned_at)
            SELECT @target, role_name, @now FROM unnest(@roles::text[]) AS role_name;
            UPDATE public.users SET updated_at=@now WHERE id=@target;
            """, connection, transaction))
        {
            replace.Parameters.AddWithValue("target", target);
            replace.Parameters.AddWithValue("roles", roles);
            replace.Parameters.AddWithValue("now", now);
            await replace.ExecuteNonQueryAsync(cancellationToken);
        }

        await AuditAsync(connection, transaction, actor, target, "RolesChanged", now, correlation, cancellationToken);
        var updated = await ReadProfileAsync(connection, transaction, target, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(updated);
    }

    public async Task<IdentityAdministrationResult<bool>> DeactivateUserAsync(
        Guid actor, Guid target, DateTimeOffset now, string? correlation, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAdministrationAsync(connection, transaction, cancellationToken);
        var profiles = await LockUsersAsync(connection, transaction, [actor, target], cancellationToken);
        var failure = CheckAdministratorAndTarget(profiles, actor, target);
        if (failure is not null) return Failure<bool>(failure.Value);
        if (await IsLastActiveAdministratorAsync(connection, transaction, profiles[target], cancellationToken))
        {
            return Failure<bool>(IdentityAdministrationFailure.LastAdmin);
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE public.users SET is_active=FALSE,updated_at=@now WHERE id=@target", connection, transaction))
        {
            update.Parameters.AddWithValue("target", target);
            update.Parameters.AddWithValue("now", now);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await RevokeUserSessionsAsync(connection, transaction, target, now, cancellationToken);
        await AuditAsync(connection, transaction, actor, target, "UserDeactivated", now, correlation, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true);
    }

    public async Task<IdentityAdministrationResult<IdentityPage<IdentityAuditRecord>>> ListAuditAsync(
        Guid actor, int page, int pageSize, CancellationToken cancellationToken)
    {
        ValidatePage(page, pageSize);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        if (!IsAdministrator(await ReadProfileAsync(connection, transaction, actor, cancellationToken)))
        {
            return Failure<IdentityPage<IdentityAuditRecord>>(IdentityAdministrationFailure.Forbidden);
        }

        await using var count = new NpgsqlCommand("SELECT COUNT(*) FROM public.audit_logs", connection, transaction);
        var total = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        await using var list = new NpgsqlCommand("""
            SELECT id,actor_user_id,action,subject_user_id,created_at,result_code
            FROM public.audit_logs ORDER BY created_at,id LIMIT @limit OFFSET @offset
            """, connection, transaction);
        AddPage(list, page, pageSize);
        var items = new List<IdentityAuditRecord>();
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new(new IdentityPage<IdentityAuditRecord>(items, total, page, pageSize));
    }

    private static async Task LockAdministrationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // Serialize all administrator writes before taking user locks. In particular,
        // two administrators cannot each observe the other as the remaining Admin.
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(7342003,1)", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<Guid, RegisteredProfile>> LockUsersAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid[] users, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {ProfileColumns} FROM public.users u
            WHERE u.id=ANY(@ids) ORDER BY u.id FOR UPDATE OF u
            """, connection, transaction);
        command.Parameters.AddWithValue("ids", users.Distinct().ToArray());
        var profiles = new Dictionary<Guid, RegisteredProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var profile = ReadProfile(reader);
            profiles.Add(profile.Id, profile);
        }

        return profiles;
    }

    private static async Task<RegisteredProfile?> ReadProfileAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {ProfileColumns} FROM public.users u WHERE u.id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
    }

    private static RegisteredProfile ReadProfile(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetFieldValue<string[]>(6), reader.GetBoolean(5));

    private static bool IsAdministrator(RegisteredProfile? profile) =>
        profile is { IsActive: true } && profile.Roles.Contains("Admin", StringComparer.Ordinal);

    private static IdentityAdministrationFailure? CheckAdministratorAndTarget(
        Dictionary<Guid, RegisteredProfile> profiles, Guid actor, Guid target)
    {
        if (!profiles.TryGetValue(actor, out var administrator) || !IsAdministrator(administrator))
        {
            return IdentityAdministrationFailure.Forbidden;
        }

        return profiles.ContainsKey(target) ? null : IdentityAdministrationFailure.NotFound;
    }

    private static async Task<bool> IsLastActiveAdministratorAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, RegisteredProfile target, CancellationToken cancellationToken)
    {
        if (!IsAdministrator(target)) return false;
        await using var command = new NpgsqlCommand("""
            SELECT COUNT(*) FROM public.users u JOIN public.user_roles ur ON ur.user_id=u.id
            WHERE u.is_active AND ur.role_name='Admin'
            """, connection, transaction);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))! <= 1;
    }

    private static async Task RevokeUserSessionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The user lock is held first, matching login, refresh and logout ordering.
        await using var command = new NpgsqlCommand("""
            UPDATE public.refresh_families SET revoked_at=COALESCE(revoked_at,@now) WHERE user_id=@target;
            UPDATE public.refresh_sessions SET revoked_at=COALESCE(revoked_at,@now) WHERE user_id=@target;
            """, connection, transaction);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid actor, Guid target, string action, DateTimeOffset now, string? correlation, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.audit_logs (actor_user_id,subject_user_id,action,created_at,result_code,correlation_id)
            VALUES (@actor,@target,@action,@now,'Success',@correlation)
            """, connection, transaction);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.Add("correlation", NpgsqlDbType.Varchar).Value = (object?)correlation ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddUserParameters(NpgsqlCommand command, Guid target, string email,
        string normalizedEmail, string fullName, bool isActive, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("id", target);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("normalized_email", normalizedEmail);
        command.Parameters.AddWithValue("full_name", fullName);
        command.Parameters.AddWithValue("active", isActive);
        command.Parameters.AddWithValue("now", now);
    }

    private static void AddPage(NpgsqlCommand command, int page, int pageSize)
    {
        command.Parameters.AddWithValue("limit", pageSize);
        command.Parameters.AddWithValue("offset", (long)(page - 1) * pageSize);
    }

    private static void ValidatePage(int page, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
    }

    private static bool IsEmailConflict(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == "users_normalized_email_key";

    private static IdentityAdministrationResult<T> Failure<T>(IdentityAdministrationFailure failure) => new(default, failure);
}
