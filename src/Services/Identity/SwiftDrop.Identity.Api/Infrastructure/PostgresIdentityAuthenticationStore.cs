using Npgsql;

using SwiftDrop.Identity.Api.Application;

namespace SwiftDrop.Identity.Api.Infrastructure;

public sealed class PostgresIdentityAuthenticationStore(NpgsqlDataSource dataSource) : IIdentityAuthenticationStore
{
    private const string AccountColumns = """
        u.id, u.email, u.full_name, u.phone_number, u.address, u.is_active,
        u.password_hash, u.locked_until,
        ARRAY(SELECT ur.role_name::text FROM public.user_roles ur
              WHERE ur.user_id = u.id ORDER BY ur.role_name)
        """;

    public async Task<IdentityAccount?> FindByEmailAsync(
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT {AccountColumns} FROM public.users u WHERE u.normalized_email = @email",
            connection);
        command.Parameters.AddWithValue("email", normalizedEmail);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAccount(reader) : null;
    }

    public async Task<IdentityAccount?> FindByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAccountAsync(connection, null, userId, false, cancellationToken);
    }

    public async Task RecordLoginFailureAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int failures;
        int lockouts;
        await using (var read = new NpgsqlCommand(
            """
            SELECT failed_login_count, lockout_count
            FROM public.users
            WHERE id = @user_id AND is_active AND password_hash IS NOT NULL
                AND (locked_until IS NULL OR locked_until <= @now)
            FOR UPDATE
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("user_id", userId);
            read.Parameters.AddWithValue("now", now);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }

            failures = reader.GetInt32(0) + 1;
            lockouts = reader.GetInt32(1);
        }

        DateTimeOffset? lockedUntil = null;
        if (failures >= 5)
        {
            failures = 0;
            lockedUntil = now.AddMinutes(Math.Min(1 << Math.Min(lockouts, 4), 15));
            lockouts = (int)Math.Min(lockouts + 1L, 5);
        }

        await using (var update = new NpgsqlCommand(
            """
            UPDATE public.users
            SET failed_login_count = @failures, lockout_count = @lockouts,
                locked_until = @locked_until, updated_at = @now
            WHERE id = @user_id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("user_id", userId);
            update.Parameters.AddWithValue("failures", failures);
            update.Parameters.AddWithValue("lockouts", lockouts);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.Add(new NpgsqlParameter("locked_until", NpgsqlTypes.NpgsqlDbType.TimestampTz)
            {
                Value = (object?)lockedUntil ?? DBNull.Value
            });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IdentityStoreResult> CreateSessionAsync(
        Guid userId,
        RefreshSessionWrite session,
        CancellationToken cancellationToken,
        string? expectedPasswordHash = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var account = await ReadAccountAsync(connection, transaction, userId, true, cancellationToken);
        if (!CanAuthenticateLocally(account, session.CreatedAt)
            || (expectedPasswordHash is not null
                && !string.Equals(account!.PasswordHash, expectedPasswordHash, StringComparison.Ordinal)))
        {
            return Invalid();
        }

        try
        {
            var expiresAt = await InsertFamilyAsync(connection, transaction, userId, session, cancellationToken);
            await ResetLoginFailuresAsync(connection, transaction, userId, session.CreatedAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IdentityStoreResult(account! with { LockedUntil = null }, SessionExpiresAt: expiresAt);
        }
        catch (PostgresException exception) when (IsGeneratedSessionConflict(exception))
        {
            return Invalid();
        }
    }

    public async Task<IdentityStoreResult> RotateAsync(
        string tokenHash,
        RefreshSessionWrite replacement,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var owner = await FindSessionOwnerAsync(connection, transaction, tokenHash, cancellationToken);
        if (owner is null)
        {
            return Invalid();
        }

        // All session operations lock the user before the family. The family lock
        // serializes both rotation and replay revocation, including different tokens.
        var account = await ReadAccountAsync(connection, transaction, owner.Value.UserId, true, cancellationToken);
        var family = await LockFamilyAsync(connection, transaction, owner.Value, cancellationToken);
        if (account is null || family is null)
        {
            return Invalid();
        }

        var presented = await LockSessionAsync(connection, transaction, tokenHash, owner.Value, cancellationToken);
        if (presented is null)
        {
            return Invalid();
        }

        if (!account.Profile.IsActive || presented.Value.ConsumedAt is not null)
        {
            await RevokeFamilyAsync(connection, transaction, owner.Value, replacement.CreatedAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Invalid();
        }

        if (family.Value.RevokedAt is not null || presented.Value.RevokedAt is not null
            || family.Value.ExpiresAt <= replacement.CreatedAt || presented.Value.ExpiresAt <= replacement.CreatedAt)
        {
            return Invalid();
        }

        var expiresAt = Earlier(replacement.ExpiresAt, family.Value.ExpiresAt);
        if (expiresAt <= replacement.CreatedAt)
        {
            return Invalid();
        }

        try
        {
            await InsertSessionAsync(connection, transaction, owner.Value.UserId, owner.Value.FamilyId,
                replacement with { ExpiresAt = expiresAt }, cancellationToken);
            await using var consume = new NpgsqlCommand(
                """
                UPDATE public.refresh_sessions
                SET consumed_at = @now, revoked_at = @now, replaced_by_session_id = @replacement_id
                WHERE id = @id
                """, connection, transaction);
            consume.Parameters.AddWithValue("now", replacement.CreatedAt);
            consume.Parameters.AddWithValue("replacement_id", replacement.Id);
            consume.Parameters.AddWithValue("id", presented.Value.Id);
            await consume.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IdentityStoreResult(account, SessionExpiresAt: expiresAt);
        }
        catch (PostgresException exception) when (IsGeneratedSessionConflict(exception))
        {
            return Invalid();
        }
    }

    public async Task<bool> LogoutAsync(
        Guid userId,
        string tokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var owner = await FindSessionOwnerAsync(connection, transaction, tokenHash, cancellationToken);
        if (owner is null || owner.Value.UserId != userId)
        {
            return false;
        }

        var account = await ReadAccountAsync(connection, transaction, userId, true, cancellationToken);
        if (account is null || await LockFamilyAsync(connection, transaction, owner.Value, cancellationToken) is null)
        {
            return false;
        }

        await RevokeFamilyAsync(connection, transaction, owner.Value, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IdentityStoreResult> AuthenticateGoogleAsync(
        VerifiedGoogleIdentity identity,
        RefreshSessionWrite session,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockGoogleSubjectAsync(connection, transaction, identity.Subject, cancellationToken);
        var linkedUser = await FindGoogleUserAsync(connection, transaction, identity.Subject, cancellationToken);
        IdentityAccount? account;
        try
        {
            if (linkedUser is { } linkedUserId)
            {
                account = await ReadAccountAsync(connection, transaction, linkedUserId, true, cancellationToken);
                if (account is null || !account.Profile.IsActive)
                {
                    return Invalid();
                }
            }
            else
            {
                var normalizedEmail = identity.Email.Trim().ToUpperInvariant();
                await using (var emailCheck = new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM public.users WHERE normalized_email = @email)",
                    connection, transaction))
                {
                    emailCheck.Parameters.AddWithValue("email", normalizedEmail);
                    if ((bool)(await emailCheck.ExecuteScalarAsync(cancellationToken) ?? false))
                    {
                        return new IdentityStoreResult(null, IdentityFailure.AccountLinkRequired);
                    }
                }

                var userId = Guid.NewGuid();
                await using (var insertUser = new NpgsqlCommand(
                    """
                    INSERT INTO public.users
                        (id, email, normalized_email, full_name, password_hash, is_active, created_at, updated_at)
                    VALUES (@id, @email, @normalized_email, @full_name, NULL, TRUE, @now, @now)
                    """, connection, transaction))
                {
                    insertUser.Parameters.AddWithValue("id", userId);
                    insertUser.Parameters.AddWithValue("email", identity.Email.Trim());
                    insertUser.Parameters.AddWithValue("normalized_email", normalizedEmail);
                    insertUser.Parameters.AddWithValue("full_name", identity.FullName.Trim());
                    insertUser.Parameters.AddWithValue("now", session.CreatedAt);
                    await insertUser.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var role = new NpgsqlCommand(
                    "INSERT INTO public.user_roles (user_id, role_name) VALUES (@id, 'Customer')",
                    connection, transaction))
                {
                    role.Parameters.AddWithValue("id", userId);
                    await role.ExecuteNonQueryAsync(cancellationToken);
                }

                await InsertGoogleIdentityAsync(connection, transaction, userId, identity.Subject,
                    session.CreatedAt, cancellationToken);
                await InsertAuditAsync(connection, transaction, userId, "UserRegistered", session.CreatedAt,
                    cancellationToken);
                account = await ReadAccountAsync(connection, transaction, userId, false, cancellationToken);
            }

            var expiresAt = await InsertFamilyAsync(connection, transaction, account!.Profile.Id,
                session, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IdentityStoreResult(account, SessionExpiresAt: expiresAt);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return new IdentityStoreResult(null, exception.ConstraintName switch
            {
                "users_normalized_email_key" => IdentityFailure.AccountLinkRequired,
                "external_identities_provider_subject_key" => IdentityFailure.SubjectConflict,
                _ => IdentityFailure.InvalidCredentials
            });
        }
    }

    public async Task<IdentityStoreResult> LinkGoogleAsync(
        Guid userId,
        string expectedPasswordHash,
        VerifiedGoogleIdentity identity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockGoogleSubjectAsync(connection, transaction, identity.Subject, cancellationToken);
        var account = await ReadAccountAsync(connection, transaction, userId, true, cancellationToken);
        if (!CanAuthenticateLocally(account, now)
            || !string.Equals(account!.PasswordHash, expectedPasswordHash, StringComparison.Ordinal))
        {
            return Invalid();
        }

        if (!string.Equals(account.Profile.Email.Trim().ToUpperInvariant(),
            identity.Email.Trim().ToUpperInvariant(), StringComparison.Ordinal))
        {
            return new IdentityStoreResult(null, IdentityFailure.EmailMismatch);
        }

        var linkedUser = await FindGoogleUserAsync(connection, transaction, identity.Subject, cancellationToken);
        if (linkedUser is not null && linkedUser != userId)
        {
            return new IdentityStoreResult(null, IdentityFailure.SubjectConflict);
        }

        try
        {
            if (linkedUser is null)
            {
                await InsertGoogleIdentityAsync(connection, transaction, userId, identity.Subject, now,
                    cancellationToken);
            }

            await ResetLoginFailuresAsync(connection, transaction, userId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IdentityStoreResult(account with { LockedUntil = null });
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.UniqueViolation
                && exception.ConstraintName == "external_identities_provider_subject_key")
        {
            return new IdentityStoreResult(null, IdentityFailure.SubjectConflict);
        }
    }

    private static async Task<IdentityAccount?> ReadAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {AccountColumns} FROM public.users u WHERE u.id = @id{(forUpdate ? " FOR UPDATE OF u" : "")}",
            connection, transaction);
        command.Parameters.AddWithValue("id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAccount(reader) : null;
    }

    private static IdentityAccount ReadAccount(NpgsqlDataReader reader) => new(
        new RegisteredProfile(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<string[]>(8), reader.GetBoolean(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));

    private static bool CanAuthenticateLocally(IdentityAccount? account, DateTimeOffset now) =>
        account is { Profile.IsActive: true, PasswordHash: not null }
        && (account.LockedUntil is null || account.LockedUntil <= now);

    private static async Task ResetLoginFailuresAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.users
            SET failed_login_count = 0, lockout_count = 0, locked_until = NULL, updated_at = @now
            WHERE id = @user_id
            """, connection, transaction);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DateTimeOffset> InsertFamilyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        RefreshSessionWrite session,
        CancellationToken cancellationToken)
    {
        var familyId = Guid.NewGuid();
        var expiresAt = Earlier(session.ExpiresAt, session.CreatedAt.AddDays(30));
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.refresh_families (id, user_id, created_at, expires_at)
            VALUES (@id, @user_id, @created_at, @expires_at)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", familyId);
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("created_at", session.CreatedAt);
            command.Parameters.AddWithValue("expires_at", expiresAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertSessionAsync(connection, transaction, userId, familyId,
            session with { ExpiresAt = expiresAt }, cancellationToken);
        return expiresAt;
    }

    private static async Task InsertSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        Guid familyId,
        RefreshSessionWrite session,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.refresh_sessions
                (id, user_id, family_id, token_hash, created_at, expires_at)
            VALUES (@id, @user_id, @family_id, @token_hash, @created_at, @expires_at)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", session.Id);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("family_id", familyId);
        command.Parameters.AddWithValue("token_hash", session.TokenHash);
        command.Parameters.AddWithValue("created_at", session.CreatedAt);
        command.Parameters.AddWithValue("expires_at", session.ExpiresAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SessionOwner?> FindSessionOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tokenHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT user_id, family_id FROM public.refresh_sessions WHERE token_hash = @token_hash",
            connection, transaction);
        command.Parameters.AddWithValue("token_hash", tokenHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SessionOwner(reader.GetGuid(0), reader.GetGuid(1))
            : null;
    }

    private static async Task<FamilyState?> LockFamilyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionOwner owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT expires_at, revoked_at FROM public.refresh_families
            WHERE id = @family_id AND user_id = @user_id FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("family_id", owner.FamilyId);
        command.Parameters.AddWithValue("user_id", owner.UserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new FamilyState(reader.GetFieldValue<DateTimeOffset>(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1))
            : null;
    }

    private static async Task<SessionState?> LockSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tokenHash,
        SessionOwner owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, expires_at, revoked_at, consumed_at FROM public.refresh_sessions
            WHERE token_hash = @token_hash AND user_id = @user_id AND family_id = @family_id FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("token_hash", tokenHash);
        command.Parameters.AddWithValue("user_id", owner.UserId);
        command.Parameters.AddWithValue("family_id", owner.FamilyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SessionState(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3))
            : null;
    }

    private static async Task RevokeFamilyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionOwner owner,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.refresh_families SET revoked_at = COALESCE(revoked_at, @now)
            WHERE id = @family_id AND user_id = @user_id;
            UPDATE public.refresh_sessions SET revoked_at = COALESCE(revoked_at, @now)
            WHERE family_id = @family_id AND user_id = @user_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("family_id", owner.FamilyId);
        command.Parameters.AddWithValue("user_id", owner.UserId);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task LockGoogleSubjectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string subject,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(7342002, hashtext(@subject))", connection, transaction);
        command.Parameters.AddWithValue("subject", subject);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid?> FindGoogleUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string subject,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT user_id FROM public.external_identities WHERE provider = 'google' AND subject = @subject",
            connection, transaction);
        command.Parameters.AddWithValue("subject", subject);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid userId ? userId : null;
    }

    private static async Task InsertGoogleIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        string subject,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.external_identities (id, user_id, provider, subject, created_at)
            VALUES (@id, @user_id, 'google', @subject, @now)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("subject", subject);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await InsertAuditAsync(connection, transaction, userId, "GoogleLinked", now, cancellationToken);
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        string action,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.audit_logs (actor_user_id, action, subject_user_id, created_at)
            VALUES (@user_id, @action, @user_id, @now)
            """, connection, transaction);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second) =>
        first <= second ? first : second;

    private static IdentityStoreResult Invalid() => new(null, IdentityFailure.InvalidCredentials);

    private static bool IsGeneratedSessionConflict(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.UniqueViolation
        && exception.ConstraintName is "refresh_sessions_pkey" or "refresh_sessions_token_hash_key"
            or "refresh_families_pkey";

    private readonly record struct SessionOwner(Guid UserId, Guid FamilyId);
    private readonly record struct FamilyState(DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
    private readonly record struct SessionState(Guid Id, DateTimeOffset ExpiresAt,
        DateTimeOffset? RevokedAt, DateTimeOffset? ConsumedAt);
}
