using System.Security.Cryptography;
using System.Text;

using Npgsql;

using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Api.Infrastructure;
using SwiftDrop.Identity.Tests.Persistence;

namespace SwiftDrop.Identity.Tests;

[Collection(IdentityPostgresCollection.Name)]
public sealed class AuthenticationAuditTests(IdentityPostgresFixture database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 4, 0, 0, TimeSpan.Zero);
    private const string StoredHash = "isolated-test-password-hash-never-an-audit-field";
    private readonly PostgresIdentityAuthenticationStore store = new(database.DataSource);

    [Fact]
    public async Task KnownUserAuthenticationLifecycleWritesSafeActorTargetResultAndTime()
    {
        var user = await LocalUserAsync();
        var first = Session(Now);
        var replacement = Session(Now.AddSeconds(1));
        await store.RecordLoginFailureAsync(user.Id, Now.AddSeconds(-1), default);
        Assert.NotNull((await store.CreateSessionAsync(user.Id, first.Write, default)).Account);
        Assert.NotNull((await store.RotateAsync(first.Write.TokenHash, replacement.Write, default)).Account);
        Assert.Equal(IdentityFailure.InvalidCredentials,
            (await store.RotateAsync(first.Write.TokenHash, Session(Now.AddSeconds(2)).Write, default)).Failure);
        Assert.True(await store.LogoutAsync(user.Id, replacement.Write.TokenHash, Now.AddSeconds(3), default));

        var records = await AuditAsync(user.Id);
        AssertAudit(records, user.Id, "LocalReauthenticationRejected", "Rejected", Now.AddSeconds(-1));
        AssertAudit(records, user.Id, "LocalLogin", "Success", Now);
        AssertAudit(records, user.Id, "SessionRefreshed", "Success", Now.AddSeconds(1));
        AssertAudit(records, user.Id, "RefreshReuseDetected", "Rejected", Now.AddSeconds(2));
        AssertAudit(records, user.Id, "Logout", "Success", Now.AddSeconds(3));
        await AssertSafeAuditAsync(user, first.Raw, replacement.Raw, first.Write.TokenHash);
    }

    [Fact]
    public async Task GoogleLoginWritesOnlyInternalActorAndTargetWithoutProviderContactData()
    {
        var identity = new VerifiedGoogleIdentity("google-audit-" + Guid.NewGuid().ToString("N"),
            NewEmail(), "Fictional Google Audit Name", null);
        var first = Session(Now);
        var result = await store.AuthenticateGoogleAsync(identity, first.Write, default);
        var account = Assert.IsType<IdentityAccount>(result.Account);
        Assert.NotNull((await store.AuthenticateGoogleAsync(identity, Session(Now.AddSeconds(1)).Write, default)).Account);

        var records = await AuditAsync(account.Profile.Id);
        var logins = records.Where(record => record.Action == "GoogleLogin").ToArray();
        Assert.Equal(2, logins.Length);
        Assert.All(logins, record =>
        {
            Assert.Equal(account.Profile.Id, record.Actor);
            Assert.Equal(account.Profile.Id, record.Target);
            Assert.Equal("Success", record.Result);
        });
        Assert.Contains(logins, record => record.CreatedAt == Now);
        Assert.Contains(logins, record => record.CreatedAt == Now.AddSeconds(1));
        AssertAudit(records, account.Profile.Id, "GoogleLinked", "Success", Now);
        AssertAudit(records, account.Profile.Id, "UserRegistered", "Success", Now);
        await AssertSafeAuditAsync(account.Profile, identity.Subject, first.Raw, first.Write.TokenHash);
    }

    [Fact]
    public async Task DisabledRefreshIsRejectedAndAuditedInTheFamilyRevocationTransaction()
    {
        var user = await LocalUserAsync();
        var first = Session(Now);
        Assert.NotNull((await store.CreateSessionAsync(user.Id, first.Write, default)).Account);
        await ExecuteAsync("UPDATE public.users SET is_active=FALSE WHERE id=@id", ("id", user.Id));

        var result = await store.RotateAsync(first.Write.TokenHash, Session(Now.AddSeconds(1)).Write, default);

        Assert.Equal(IdentityFailure.InvalidCredentials, result.Failure);
        AssertAudit(await AuditAsync(user.Id), user.Id, "DisabledRefreshRejected", "Rejected", Now.AddSeconds(1));
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_families WHERE user_id=@id AND revoked_at IS NOT NULL", ("id", user.Id)));
    }

    [Theory]
    [InlineData("LocalLogin")]
    [InlineData("SessionRefreshed")]
    [InlineData("Logout")]
    [InlineData("LocalReauthenticationRejected")]
    [InlineData("RefreshReuseDetected")]
    [InlineData("DisabledRefreshRejected")]
    public async Task RejectedAuditInsertRollsBackSessionFamilyAndFailureCounterChanges(string action)
    {
        var user = await LocalUserAsync();
        var first = Session(Now);
        Assert.NotNull((await store.CreateSessionAsync(user.Id, first.Write, default)).Account);
        if (action == "RefreshReuseDetected")
            Assert.NotNull((await store.RotateAsync(first.Write.TokenHash, Session(Now.AddSeconds(1)).Write, default)).Account);
        if (action == "DisabledRefreshRejected")
            await ExecuteAsync("UPDATE public.users SET is_active=FALSE WHERE id=@id", ("id", user.Id));
        await ExecuteAsync("UPDATE public.users SET failed_login_count=3 WHERE id=@id", ("id", user.Id));
        var before = await StateAsync(user.Id);

        await WithRejectedAuditAsync(action, async () =>
        {
            await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                switch (action)
                {
                    case "LocalLogin": await store.CreateSessionAsync(user.Id, Session(Now.AddSeconds(2)).Write, default); break;
                    case "LocalReauthenticationRejected": await store.RecordLoginFailureAsync(user.Id, Now.AddSeconds(2), default); break;
                    case "Logout": await store.LogoutAsync(user.Id, first.Write.TokenHash, Now.AddSeconds(2), default); break;
                    default: await store.RotateAsync(first.Write.TokenHash, Session(Now.AddSeconds(2)).Write, default); break;
                }
            });
        });

        Assert.Equal(before, await StateAsync(user.Id));
    }

    [Fact]
    public async Task GoogleLoginAuditFailureRollsBackAnExistingUsersNewFamilyAndSession()
    {
        var identity = new VerifiedGoogleIdentity("google-audit-" + Guid.NewGuid().ToString("N"), NewEmail(), "Audit Google User", null);
        var user = Assert.IsType<IdentityAccount>((await store.AuthenticateGoogleAsync(identity, Session(Now).Write, default)).Account);
        var before = await StateAsync(user.Profile.Id);

        await WithRejectedAuditAsync("GoogleLogin", async () =>
            await Assert.ThrowsAsync<PostgresException>(() =>
                store.AuthenticateGoogleAsync(identity, Session(Now.AddSeconds(1)).Write, default)));

        Assert.Equal(before, await StateAsync(user.Profile.Id));
    }

    [Fact]
    public async Task GoogleLoginAuditFailureRollsBackNewUserRoleExternalLinkAndSession()
    {
        var identity = new VerifiedGoogleIdentity("google-audit-" + Guid.NewGuid().ToString("N"), NewEmail(), "Audit Google User", null);
        var before = await TableCountsAsync();

        await WithRejectedAuditAsync("GoogleLogin", async () =>
            await Assert.ThrowsAsync<PostgresException>(() =>
                store.AuthenticateGoogleAsync(identity, Session(Now).Write, default)));

        Assert.Equal(before, await TableCountsAsync());
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.users WHERE normalized_email=@email", ("email", identity.Email.ToUpperInvariant())));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.external_identities WHERE subject=@subject", ("subject", identity.Subject)));
    }

    [Fact]
    public async Task ForeignLogoutAndUnknownRefreshHaveNoAuditOrSessionSideEffects()
    {
        var owner = await LocalUserAsync();
        var outsider = await LocalUserAsync();
        var session = Session(Now);
        Assert.NotNull((await store.CreateSessionAsync(owner.Id, session.Write, default)).Account);
        var before = await StateAsync(owner.Id);
        var outsiderBefore = await StateAsync(outsider.Id);

        Assert.False(await store.LogoutAsync(outsider.Id, session.Write.TokenHash, Now.AddSeconds(1), default));
        Assert.Equal(IdentityFailure.InvalidCredentials,
            (await store.RotateAsync(Session(Now).Write.TokenHash, Session(Now.AddSeconds(1)).Write, default)).Failure);

        Assert.Equal(before, await StateAsync(owner.Id));
        Assert.Equal(outsiderBefore, await StateAsync(outsider.Id));
    }

    private async Task<RegisteredProfile> LocalUserAsync()
    {
        var email = NewEmail();
        var user = await new PostgresUserRegistrationStore(database.DataSource)
            .RegisterAsync(email, email.ToUpperInvariant(), "Fictional Audit Customer", StoredHash, default);
        var profile = Assert.IsType<RegisteredProfile>(user);
        await ExecuteAsync("UPDATE public.users SET phone_number='0770000000',address='Fictional Audit Address' WHERE id=@id", ("id", profile.Id));
        return profile;
    }

    private async Task<IReadOnlyList<AuditRecord>> AuditAsync(Guid userId)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT actor_user_id, subject_user_id, action, result_code, created_at
            FROM public.audit_logs WHERE subject_user_id=@id ORDER BY id
            """);
        command.Parameters.AddWithValue("id", userId);
        await using var reader = await command.ExecuteReaderAsync();
        var records = new List<AuditRecord>();
        while (await reader.ReadAsync()) records.Add(new AuditRecord(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
            reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return records;
    }

    private static void AssertAudit(IReadOnlyList<AuditRecord> records, Guid userId, string action, string result, DateTimeOffset timestamp)
    {
        var record = Assert.Single(records, record => record.Action == action);
        Assert.Equal(new AuditRecord(userId, userId, action, result, timestamp), record);
    }

    private async Task AssertSafeAuditAsync(RegisteredProfile user, params string[] secrets)
    {
        var serialized = await ScalarAsync<string>("""
            SELECT COALESCE(jsonb_agg(to_jsonb(a) ORDER BY a.id),'[]'::jsonb)::text
            FROM public.audit_logs a WHERE subject_user_id=@id
            """, ("id", user.Id));
        foreach (var forbidden in secrets.Concat([user.Email, user.FullName, StoredHash, "0770000000", "Fictional Audit Address"]))
            Assert.DoesNotContain(forbidden, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("password", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", serialized, StringComparison.OrdinalIgnoreCase);
    }

    private Task<string> StateAsync(Guid userId) => ScalarAsync<string>("""
        SELECT jsonb_build_object('failed',u.failed_login_count,'lockouts',u.lockout_count,'locked',u.locked_until,
            'updated',u.updated_at,'active',u.is_active,
            'families',(SELECT COALESCE(jsonb_agg(jsonb_build_object('id',f.id,'revoked',f.revoked_at) ORDER BY f.id),'[]'::jsonb)
                FROM public.refresh_families f WHERE f.user_id=u.id),
            'sessions',(SELECT COALESCE(jsonb_agg(jsonb_build_object('id',s.id,'revoked',s.revoked_at,
                'consumed',s.consumed_at,'replacement',s.replaced_by_session_id) ORDER BY s.id),'[]'::jsonb)
                FROM public.refresh_sessions s WHERE s.user_id=u.id),
            'audits',(SELECT COUNT(*) FROM public.audit_logs a WHERE a.subject_user_id=u.id))::text
        FROM public.users u WHERE u.id=@id
        """, ("id", userId));

    private Task<string> TableCountsAsync() => ScalarAsync<string>("""
        SELECT jsonb_build_array((SELECT COUNT(*) FROM public.users), (SELECT COUNT(*) FROM public.user_roles),
            (SELECT COUNT(*) FROM public.external_identities), (SELECT COUNT(*) FROM public.refresh_families),
            (SELECT COUNT(*) FROM public.refresh_sessions), (SELECT COUNT(*) FROM public.audit_logs))::text
        """);

    private async Task WithRejectedAuditAsync(string action, Func<Task> assertion)
    {
        Assert.Contains(action, new[] { "LocalLogin", "SessionRefreshed", "Logout", "LocalReauthenticationRejected",
            "RefreshReuseDetected", "DisabledRefreshRejected", "GoogleLogin" });
        await ExecuteAsync($"""
            CREATE FUNCTION public.identity_auth_audit_reject() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.action = '{action}' THEN RAISE EXCEPTION 'Deliberate isolated auth audit rejection'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER identity_auth_audit_reject BEFORE INSERT ON public.audit_logs
            FOR EACH ROW EXECUTE FUNCTION public.identity_auth_audit_reject();
            """);
        try { await assertion(); }
        finally
        {
            await ExecuteAsync("DROP TRIGGER identity_auth_audit_reject ON public.audit_logs; DROP FUNCTION public.identity_auth_audit_reject()");
        }
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Assert.IsType<T>(await command.ExecuteScalarAsync());
    }

    private static string NewEmail() => "auth-audit-" + Guid.NewGuid().ToString("N") + "@example.test";
    private static SessionFixture Session(DateTimeOffset now)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return new SessionFixture(raw, new RefreshSessionWrite(Guid.NewGuid(), hash, now, now.AddDays(30)));
    }

    private sealed record SessionFixture(string Raw, RefreshSessionWrite Write);
    private sealed record AuditRecord(Guid Actor, Guid Target, string Action, string Result, DateTimeOffset CreatedAt);
}
