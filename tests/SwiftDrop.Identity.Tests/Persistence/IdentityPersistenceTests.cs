using System.Security.Cryptography;
using System.Text;

using Npgsql;

using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Api.Infrastructure;

namespace SwiftDrop.Identity.Tests.Persistence;

[Collection(IdentityPostgresCollection.Name)]
public sealed class IdentityPersistenceTests(IdentityPostgresFixture database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
    private const string PasswordHash = "$argon2id$v=19$m=19456,t=2,p=1$cGVyc2lzdGVuY2U$dGVzdC1oYXNo";
    private readonly PostgresIdentityAuthenticationStore _store = new(database.DataSource);
    private readonly PostgresUserRegistrationStore _registration = new(database.DataSource);

    [Fact]
    public async Task RegistrationPersistsOnlyCustomerWithNormalizedUniqueEmailAndPasswordHash()
    {
        var email = NewEmail();
        var profile = await _registration.RegisterAsync(email, Normalize(email), "Customer", PasswordHash, default);
        Assert.NotNull(profile);
        Assert.Equal(["Customer"], profile.Roles);
        Assert.True(profile.IsActive);
        var persisted = await _store.FindByEmailAsync(Normalize(email), default);
        Assert.NotNull(persisted);
        Assert.Equal(profile.Id, persisted.Profile.Id);
        Assert.Equal(PasswordHash, persisted.PasswordHash);
        Assert.Equal(1L, await CountAsync("user_roles", "user_id", profile.Id));
        Assert.Equal(1L, await CountAsync("audit_logs", "subject_user_id", profile.Id));

        Assert.Null(await _registration.RegisterAsync(email.ToUpperInvariant(), Normalize(email), "Duplicate", PasswordHash, default));
        Assert.Equal(1L, await CountAsync("users", "normalized_email", Normalize(email)));
        Assert.Equal(1L, await CountAsync("audit_logs", "subject_user_id", profile.Id));
    }

    [Fact]
    public async Task ConcurrentRegistrationHasExactlyOneAccountRoleAndAudit()
    {
        var email = NewEmail();
        var results = await ConcurrentAsync(6, _ =>
            _registration.RegisterAsync(email, Normalize(email), "Concurrent Customer", PasswordHash, default));
        var winner = Assert.Single(results, result => result is not null);
        Assert.NotNull(winner);
        Assert.Equal(1L, await CountAsync("users", "normalized_email", Normalize(email)));
        Assert.Equal(1L, await CountAsync("user_roles", "user_id", winner.Id));
        Assert.Equal(1L, await CountAsync("audit_logs", "subject_user_id", winner.Id));
    }

    [Fact]
    public async Task RegistrationRollsBackUserAndRoleWhenAuditWriteFails()
    {
        var email = NewEmail();
        await WithRejectedAuditAsync("UserRegistered", async () =>
        {
            await Assert.ThrowsAsync<PostgresException>(() =>
                _registration.RegisterAsync(email, Normalize(email), "Rollback", PasswordHash, default));
        });

        Assert.Equal(0L, await CountAsync("users", "normalized_email", Normalize(email)));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.user_roles ur LEFT JOIN public.users u ON ur.user_id=u.id WHERE u.id IS NULL"));
    }

    [Fact]
    public async Task SchemaEnforcesRolesAndForeignKeys()
    {
        Assert.Equal(5L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.roles WHERE name IN ('Customer','Courier','Merchant','Dispatcher','Admin')"));
        var user = await LocalAsync();
        await AssertSqlStateAsync(PostgresErrorCodes.UniqueViolation,
            "INSERT INTO public.user_roles (user_id, role_name) VALUES (@id, 'Customer')", ("id", user.Id));
        await AssertSqlStateAsync(PostgresErrorCodes.ForeignKeyViolation,
            "INSERT INTO public.user_roles (user_id, role_name) VALUES (@id, 'MissingRole')", ("id", user.Id));
        await AssertSqlStateAsync(PostgresErrorCodes.ForeignKeyViolation,
            "INSERT INTO public.user_roles (user_id, role_name) VALUES (@id, 'Customer')", ("id", Guid.NewGuid()));
        await AssertSqlStateAsync(PostgresErrorCodes.ForeignKeyViolation,
            "INSERT INTO public.external_identities (id,user_id,provider,subject) VALUES (@id,@user,'google',@subject)",
            ("id", Guid.NewGuid()), ("user", Guid.NewGuid()), ("subject", Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task GoogleSignupCreatesPasswordlessCustomerExternalIdentityAndSessionAtomically()
    {
        var identity = Google();
        var session = Session();
        var result = await _store.AuthenticateGoogleAsync(identity, session, default);
        var account = AssertSuccess(result);
        Assert.Null(account.PasswordHash);
        Assert.Equal(["Customer"], account.Profile.Roles);
        Assert.Equal(identity.Email, account.Profile.Email);
        Assert.Equal(1L, await CountAsync("external_identities", "user_id", account.Profile.Id));
        Assert.Equal(1L, await CountAsync("refresh_sessions", "user_id", account.Profile.Id));
        Assert.Equal(2L, await CountAsync("audit_logs", "subject_user_id", account.Profile.Id));
        AssertFailure(await _store.CreateSessionAsync(account.Profile.Id, Session(), default));
    }

    [Fact]
    public async Task GoogleSubjectRatherThanEmailResolvesExistingUserAndCurrentRoles()
    {
        var identity = Google();
        var first = AssertSuccess(await _store.AuthenticateGoogleAsync(identity, Session(), default));
        await ExecuteAsync("INSERT INTO public.user_roles (user_id,role_name) VALUES (@id,'Courier')", ("id", first.Profile.Id));

        var second = AssertSuccess(await _store.AuthenticateGoogleAsync(
            identity with { Email = NewEmail(), FullName = "Changed Google metadata" }, Session(), default));
        Assert.Equal(first.Profile.Id, second.Profile.Id);
        Assert.Equal(first.Profile.Email, second.Profile.Email);
        Assert.Equal(["Courier", "Customer"], second.Profile.Roles);
        Assert.Equal(1L, await CountAsync("external_identities", "subject", identity.Subject));
        Assert.Equal(2L, await CountAsync("refresh_sessions", "user_id", first.Profile.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GoogleEmailCollisionNeverAutomaticallyLinksOrCreatesSession(bool passwordless)
    {
        var existing = passwordless
            ? AssertSuccess(await _store.AuthenticateGoogleAsync(Google(), Session(), default)).Profile
            : await LocalAsync();
        var beforeSessions = await CountAsync("refresh_sessions", "user_id", existing.Id);
        var beforeIdentities = await CountAsync("external_identities", "user_id", existing.Id);
        var identity = Google(existing.Email.ToUpperInvariant());

        var result = await _store.AuthenticateGoogleAsync(identity, Session(), default);
        AssertFailure(result, IdentityFailure.AccountLinkRequired);
        Assert.Equal(0L, await CountAsync("external_identities", "subject", identity.Subject));
        Assert.Equal(beforeIdentities, await CountAsync("external_identities", "user_id", existing.Id));
        Assert.Equal(beforeSessions, await CountAsync("refresh_sessions", "user_id", existing.Id));
        Assert.Equal(1L, await CountAsync("users", "normalized_email", Normalize(existing.Email)));
    }

    [Fact]
    public async Task DisabledGoogleAccountCannotCreateAnotherSession()
    {
        var identity = Google();
        var account = AssertSuccess(await _store.AuthenticateGoogleAsync(identity, Session(), default));
        await ExecuteAsync("UPDATE public.users SET is_active=FALSE WHERE id=@id", ("id", account.Profile.Id));
        AssertFailure(await _store.AuthenticateGoogleAsync(identity, Session(), default));
        Assert.Equal(1L, await CountAsync("refresh_sessions", "user_id", account.Profile.Id));
    }

    [Fact]
    public async Task ConcurrentGoogleSubjectCreatesOneAccountAndOneIdentity()
    {
        var identity = Google();
        var results = await ConcurrentAsync(6, _ => _store.AuthenticateGoogleAsync(identity, Session(), default));
        var accounts = results.Select(AssertSuccess).ToArray();
        var accountId = Assert.Single(accounts.Select(account => account.Profile.Id).Distinct());
        Assert.Equal(1L, await CountAsync("users", "normalized_email", Normalize(identity.Email)));
        Assert.Equal(1L, await CountAsync("external_identities", "subject", identity.Subject));
        Assert.Equal(6L, await CountAsync("refresh_sessions", "user_id", accountId));
    }

    [Fact]
    public async Task ConcurrentGoogleSubjectsWithSameEmailNeverMerge()
    {
        var email = NewEmail();
        var identities = Enumerable.Range(0, 6).Select(_ => Google(email)).ToArray();
        var results = await ConcurrentAsync(identities.Length,
            index => _store.AuthenticateGoogleAsync(identities[index], Session(), default));
        var winner = Assert.Single(results, result => result.Account is not null);
        Assert.All(results.Where(result => result.Account is null), result => AssertFailure(result, IdentityFailure.AccountLinkRequired));
        Assert.Equal(1L, await CountAsync("users", "normalized_email", Normalize(email)));
        Assert.Equal(1L, await CountAsync("external_identities", "user_id", winner.Account!.Profile.Id));
        Assert.Equal(1L, await CountAsync("refresh_sessions", "user_id", winner.Account.Profile.Id));
    }

    [Fact]
    public async Task GoogleSignupRollsBackAccountExternalIdentityRoleAndAuditWhenSessionConflicts()
    {
        var local = await LocalAsync();
        var occupied = Session();
        AssertSuccess(await _store.CreateSessionAsync(local.Id, occupied, default));
        var identity = Google();
        AssertFailure(await _store.AuthenticateGoogleAsync(identity, Session() with { TokenHash = occupied.TokenHash }, default));
        Assert.Equal(0L, await CountAsync("users", "normalized_email", Normalize(identity.Email)));
        Assert.Equal(0L, await CountAsync("external_identities", "subject", identity.Subject));
        Assert.Equal(1L, await CountAsync("refresh_families", "user_id", local.Id));
    }

    [Fact]
    public async Task GoogleSignupRollsBackEveryWriteWhenAuditFails()
    {
        var identity = Google();
        await WithRejectedAuditAsync("UserRegistered", async () =>
        {
            await Assert.ThrowsAsync<PostgresException>(() => _store.AuthenticateGoogleAsync(identity, Session(), default));
        });
        Assert.Equal(0L, await CountAsync("users", "normalized_email", Normalize(identity.Email)));
        Assert.Equal(0L, await CountAsync("external_identities", "subject", identity.Subject));
    }

    [Fact]
    public async Task ExplicitGoogleLinkRequiresLocalHashAndMatchingEmailAndIsIdempotent()
    {
        var local = await LocalAsync();
        var identity = Google(" " + local.Email.ToUpperInvariant() + " ");
        Assert.Equal(local.Id, AssertSuccess(await _store.LinkGoogleAsync(local.Id, PasswordHash, identity, Now, default)).Profile.Id);
        AssertSuccess(await _store.LinkGoogleAsync(local.Id, PasswordHash, identity, Now, default));
        Assert.Equal(1L, await CountAsync("external_identities", "subject", identity.Subject));
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.audit_logs WHERE subject_user_id=@id AND action='GoogleLinked'", ("id", local.Id)));
        Assert.Equal(0L, await CountAsync("refresh_sessions", "user_id", local.Id));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("passwordless")]
    [InlineData("changed-password")]
    [InlineData("locked")]
    [InlineData("missing")]
    public async Task ExplicitGoogleLinkRejectsInvalidLocalReauthentication(string condition)
    {
        var local = await LocalAsync();
        switch (condition)
        {
            case "disabled":
                await ExecuteAsync("UPDATE public.users SET is_active=FALSE WHERE id=@id", ("id", local.Id));
                break;
            case "passwordless":
                await ExecuteAsync("UPDATE public.users SET password_hash=NULL WHERE id=@id", ("id", local.Id));
                break;
            case "changed-password":
                await ExecuteAsync("UPDATE public.users SET password_hash='changed-hash' WHERE id=@id", ("id", local.Id));
                break;
            case "locked":
                await ExecuteAsync("UPDATE public.users SET locked_until=@until WHERE id=@id", ("id", local.Id), ("until", Now.AddMinutes(1)));
                break;
        }

        var identity = Google(local.Email);
        var result = await _store.LinkGoogleAsync(condition == "missing" ? Guid.NewGuid() : local.Id,
            PasswordHash, identity, Now, default);
        AssertFailure(result);
        Assert.Equal(0L, await CountAsync("external_identities", "subject", identity.Subject));
    }

    [Fact]
    public async Task ExplicitGoogleLinkRejectsEmailMismatchWithoutMutation()
    {
        var local = await LocalAsync();
        var identity = Google();
        AssertFailure(await _store.LinkGoogleAsync(local.Id, PasswordHash, identity, Now, default), IdentityFailure.EmailMismatch);
        Assert.Equal(0L, await CountAsync("external_identities", "subject", identity.Subject));
    }

    [Fact]
    public async Task GoogleSubjectCannotBeReassignedToAnotherAccountEvenWithMatchingEmail()
    {
        var first = await LocalAsync();
        var second = await LocalAsync();
        var identity = Google(first.Email);
        AssertSuccess(await _store.LinkGoogleAsync(first.Id, PasswordHash, identity, Now, default));
        AssertFailure(await _store.LinkGoogleAsync(second.Id, PasswordHash, identity with { Email = second.Email }, Now, default),
            IdentityFailure.SubjectConflict);
        Assert.Equal(first.Id, await ScalarAsync<Guid>(
            "SELECT user_id FROM public.external_identities WHERE provider='google' AND subject=@subject", ("subject", identity.Subject)));
        await AssertSqlStateAsync(PostgresErrorCodes.UniqueViolation,
            "INSERT INTO public.external_identities (id,user_id,provider,subject) VALUES (@id,@user,'google',@subject)",
            ("id", Guid.NewGuid()), ("user", second.Id), ("subject", identity.Subject));
    }

    [Fact]
    public async Task ExplicitGoogleLinkRollsBackExternalIdentityWhenAuditFails()
    {
        var local = await LocalAsync();
        var identity = Google(local.Email);
        await WithRejectedAuditAsync("GoogleLinked", async () =>
        {
            await Assert.ThrowsAsync<PostgresException>(() => _store.LinkGoogleAsync(local.Id, PasswordHash, identity, Now, default));
        });
        Assert.Equal(0L, await CountAsync("external_identities", "subject", identity.Subject));
    }

    [Fact]
    public async Task SessionPersistsHashAndCapsFamilyAndTokenAtThirtyDays()
    {
        var local = await LocalAsync();
        var plaintext = "opaque-refresh-token-material-" + Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
        var session = Session() with { TokenHash = hash, ExpiresAt = Now.AddDays(90) };
        var result = await _store.CreateSessionAsync(local.Id, session, default);
        AssertSuccess(result);
        Assert.Equal(Now.AddDays(30), result.SessionExpiresAt);
        Assert.Equal(hash, await ScalarAsync<string>("SELECT token_hash FROM public.refresh_sessions WHERE id=@id", ("id", session.Id)));
        Assert.Equal(0L, await CountAsync("refresh_sessions", "token_hash", plaintext));
        Assert.Equal(Now.AddDays(30), await TimestampAsync("SELECT expires_at FROM public.refresh_sessions WHERE id=@id", ("id", session.Id)));
        Assert.Equal(Now.AddDays(30), await TimestampAsync("SELECT expires_at FROM public.refresh_families WHERE user_id=@id", ("id", local.Id)));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='refresh_sessions' AND column_name IN ('token','refresh_token','access_token')"));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("locked")]
    [InlineData("passwordless")]
    [InlineData("missing")]
    public async Task LocalSessionCreationRequiresActiveUnlockedLocalAccount(string condition)
    {
        var local = await LocalAsync();
        if (condition == "disabled")
        {
            await ExecuteAsync("UPDATE public.users SET is_active=FALSE WHERE id=@id", ("id", local.Id));
        }
        else if (condition == "locked")
        {
            await ExecuteAsync("UPDATE public.users SET locked_until=@until WHERE id=@id", ("id", local.Id), ("until", Now.AddMinutes(1)));
        }
        else if (condition == "passwordless")
        {
            await ExecuteAsync("UPDATE public.users SET password_hash=NULL WHERE id=@id", ("id", local.Id));
        }

        AssertFailure(await _store.CreateSessionAsync(condition == "missing" ? Guid.NewGuid() : local.Id, Session(), default));
        Assert.Equal(0L, await CountAsync("refresh_families", "user_id", local.Id));
        Assert.Equal(0L, await CountAsync("refresh_sessions", "user_id", local.Id));
    }

    [Fact]
    public async Task LocalSessionCreationRejectsCredentialChangedAfterPasswordVerification()
    {
        var local = await LocalAsync();
        var verifiedAccount = await _store.FindByIdAsync(local.Id, default);
        Assert.NotNull(verifiedAccount);
        const string changedHash = "replacement-local-password-hash";
        await ExecuteAsync("UPDATE public.users SET password_hash=@hash WHERE id=@id",
            ("hash", changedHash), ("id", local.Id));

        AssertFailure(await _store.CreateSessionAsync(local.Id, Session(), default, verifiedAccount.PasswordHash));
        Assert.Equal(0L, await CountAsync("refresh_families", "user_id", local.Id));
        Assert.Equal(0L, await CountAsync("refresh_sessions", "user_id", local.Id));

        AssertSuccess(await _store.CreateSessionAsync(local.Id, Session(), default, changedHash));
        Assert.Equal(1L, await CountAsync("refresh_families", "user_id", local.Id));
        Assert.Equal(1L, await CountAsync("refresh_sessions", "user_id", local.Id));
    }

    [Fact]
    public async Task RotationConsumesPresentedTokenAndKeepsOriginalFamilyExpiry()
    {
        var local = await LocalAsync();
        var initial = Session();
        AssertSuccess(await _store.CreateSessionAsync(local.Id, initial, default));
        var replacement = Session(Now.AddDays(29));
        var result = await _store.RotateAsync(initial.TokenHash, replacement, default);
        AssertSuccess(result);
        Assert.Equal(initial.ExpiresAt, result.SessionExpiresAt);
        Assert.Equal(replacement.Id, await ScalarAsync<Guid>(
            "SELECT replaced_by_session_id FROM public.refresh_sessions WHERE id=@id", ("id", initial.Id)));
        Assert.Equal(replacement.CreatedAt, await TimestampAsync(
            "SELECT consumed_at FROM public.refresh_sessions WHERE id=@id", ("id", initial.Id)));
        Assert.Equal(replacement.CreatedAt, await TimestampAsync(
            "SELECT revoked_at FROM public.refresh_sessions WHERE id=@id", ("id", initial.Id)));
        Assert.Equal(initial.ExpiresAt, await TimestampAsync(
            "SELECT expires_at FROM public.refresh_sessions WHERE id=@id", ("id", replacement.Id)));
        Assert.Equal(1L, await CountAsync("refresh_families", "user_id", local.Id));
        Assert.Equal(2L, await CountAsync("refresh_sessions", "user_id", local.Id));
    }

    [Fact]
    public async Task ConsumedTokenReuseRevokesEntireFamilyButPreservesOtherLogin()
    {
        var local = await LocalAsync();
        var initial = Session();
        var otherLogin = Session();
        AssertSuccess(await _store.CreateSessionAsync(local.Id, initial, default));
        AssertSuccess(await _store.CreateSessionAsync(local.Id, otherLogin, default));
        var replacement = Session(Now.AddMinutes(1));
        AssertSuccess(await _store.RotateAsync(initial.TokenHash, replacement, default));
        AssertFailure(await _store.RotateAsync(initial.TokenHash, Session(Now.AddMinutes(2)), default));
        AssertFailure(await _store.RotateAsync(replacement.TokenHash, Session(Now.AddMinutes(3)), default));
        AssertSuccess(await _store.RotateAsync(otherLogin.TokenHash, Session(Now.AddMinutes(3)), default));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_sessions WHERE family_id=(SELECT family_id FROM public.refresh_sessions WHERE id=@id) AND revoked_at IS NULL",
            ("id", initial.Id)));
    }

    [Fact]
    public async Task ConcurrentReuseAllowsOneRotationAndRevokesItsReplacement()
    {
        var local = await LocalAsync();
        var initial = Session();
        AssertSuccess(await _store.CreateSessionAsync(local.Id, initial, default));
        var replacements = Enumerable.Range(0, 6).Select(_ => Session(Now.AddMinutes(1))).ToArray();
        var results = await ConcurrentAsync(replacements.Length,
            index => _store.RotateAsync(initial.TokenHash, replacements[index], default));
        Assert.Single(results, result => result.Account is not null);
        Assert.All(results.Where(result => result.Account is null), result => AssertFailure(result));
        Assert.Equal(2L, await CountAsync("refresh_sessions", "user_id", local.Id));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_sessions WHERE user_id=@id AND revoked_at IS NULL", ("id", local.Id)));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_families WHERE user_id=@id AND revoked_at IS NULL", ("id", local.Id)));
    }

    [Fact]
    public async Task LogoutRequiresOwnerAndRevokesOnlySelectedFamily()
    {
        var local = await LocalAsync();
        var otherUser = await LocalAsync();
        var initial = Session();
        var otherLogin = Session();
        AssertSuccess(await _store.CreateSessionAsync(local.Id, initial, default));
        AssertSuccess(await _store.CreateSessionAsync(local.Id, otherLogin, default));
        var replacement = Session(Now.AddMinutes(1));
        AssertSuccess(await _store.RotateAsync(initial.TokenHash, replacement, default));
        Assert.False(await _store.LogoutAsync(otherUser.Id, replacement.TokenHash, Now.AddMinutes(2), default));
        Assert.False(await _store.LogoutAsync(local.Id, "unknown-hash", Now.AddMinutes(2), default));
        Assert.True(await _store.LogoutAsync(local.Id, initial.TokenHash, Now.AddMinutes(2), default));
        Assert.True(await _store.LogoutAsync(local.Id, replacement.TokenHash, Now.AddMinutes(3), default));
        AssertFailure(await _store.RotateAsync(replacement.TokenHash, Session(Now.AddMinutes(4)), default));
        AssertSuccess(await _store.RotateAsync(otherLogin.TokenHash, Session(Now.AddMinutes(4)), default));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("expired")]
    [InlineData("revoked-token")]
    [InlineData("revoked-family")]
    [InlineData("missing")]
    public async Task RefreshRejectsDisabledExpiredRevokedAndUnknownSessions(string condition)
    {
        var local = await LocalAsync();
        var initial = Session();
        AssertSuccess(await _store.CreateSessionAsync(local.Id, initial, default));
        if (condition == "disabled")
        {
            await ExecuteAsync("UPDATE public.users SET is_active=FALSE WHERE id=@id", ("id", local.Id));
        }
        else if (condition == "revoked-token")
        {
            await ExecuteAsync("UPDATE public.refresh_sessions SET revoked_at=@now WHERE id=@id", ("id", initial.Id), ("now", Now));
        }
        else if (condition == "revoked-family")
        {
            await ExecuteAsync("UPDATE public.refresh_families SET revoked_at=@now WHERE user_id=@id", ("id", local.Id), ("now", Now));
        }

        AssertFailure(await _store.RotateAsync(condition == "missing" ? "missing-hash" : initial.TokenHash,
            Session(condition == "expired" ? initial.ExpiresAt : Now.AddMinutes(1)), default));
        Assert.Equal(1L, await CountAsync("refresh_sessions", "user_id", local.Id));
        if (condition == "disabled")
        {
            Assert.Equal(1L, await ScalarAsync<long>(
                "SELECT COUNT(*) FROM public.refresh_families WHERE user_id=@id AND revoked_at IS NOT NULL", ("id", local.Id)));
        }
    }

    [Fact]
    public async Task FailedSessionInsertionRollsBackNewFamilyAndPreservesLoginFailures()
    {
        var local = await LocalAsync();
        var initial = Session();
        AssertSuccess(await _store.CreateSessionAsync(local.Id, initial, default));
        await _store.RecordLoginFailureAsync(local.Id, Now, default);
        AssertFailure(await _store.CreateSessionAsync(local.Id, Session() with { TokenHash = initial.TokenHash }, default));
        Assert.Equal(1L, await CountAsync("refresh_families", "user_id", local.Id));
        Assert.Equal(1L, await CountAsync("refresh_sessions", "user_id", local.Id));
        Assert.Equal(1, await ScalarAsync<int>("SELECT failed_login_count FROM public.users WHERE id=@id", ("id", local.Id)));
    }

    [Fact]
    public async Task FailedReplacementInsertionLeavesOriginalSessionUsable()
    {
        var local = await LocalAsync();
        var initial = Session();
        var other = Session();
        AssertSuccess(await _store.CreateSessionAsync(local.Id, initial, default));
        AssertSuccess(await _store.CreateSessionAsync(local.Id, other, default));
        AssertFailure(await _store.RotateAsync(initial.TokenHash, Session(Now.AddMinutes(1)) with { TokenHash = other.TokenHash }, default));
        Assert.Equal(2L, await CountAsync("refresh_sessions", "user_id", local.Id));
        AssertSuccess(await _store.RotateAsync(initial.TokenHash, Session(Now.AddMinutes(2)), default));
    }

    [Fact]
    public async Task RefreshSchemaRejectsCrossUserFamiliesAndInconsistentRotationAndExpiry()
    {
        var first = await LocalAsync();
        var second = await LocalAsync();
        var firstSession = Session();
        var secondSession = Session();
        AssertSuccess(await _store.CreateSessionAsync(first.Id, firstSession, default));
        AssertSuccess(await _store.CreateSessionAsync(second.Id, secondSession, default));
        await AssertSqlStateAsync(PostgresErrorCodes.ForeignKeyViolation,
            "UPDATE public.refresh_sessions SET user_id=@other WHERE id=@id", ("other", second.Id), ("id", firstSession.Id));
        await AssertSqlStateAsync(PostgresErrorCodes.CheckViolation,
            "UPDATE public.refresh_sessions SET expires_at=created_at WHERE id=@id", ("id", firstSession.Id));
        await AssertSqlStateAsync(PostgresErrorCodes.CheckViolation,
            "UPDATE public.refresh_sessions SET consumed_at=created_at WHERE id=@id", ("id", firstSession.Id));
        await AssertSqlStateAsync(PostgresErrorCodes.CheckViolation,
            "UPDATE public.refresh_sessions SET consumed_at=created_at,replaced_by_session_id=id WHERE id=@id", ("id", firstSession.Id));
        await AssertSqlStateAsync(PostgresErrorCodes.ForeignKeyViolation,
            "UPDATE public.refresh_sessions SET consumed_at=created_at,replaced_by_session_id=@other WHERE id=@id",
            ("other", secondSession.Id), ("id", firstSession.Id));
        await AssertSqlStateAsync(PostgresErrorCodes.CheckViolation,
            "UPDATE public.refresh_families SET expires_at=created_at WHERE user_id=@id", ("id", first.Id));
    }

    [Fact]
    public async Task FiveConcurrentLoginFailuresLockAccountWithoutLostUpdates()
    {
        var local = await LocalAsync();
        await ConcurrentAsync(5, async _ =>
        {
            await _store.RecordLoginFailureAsync(local.Id, Now, default);
            return true;
        });
        Assert.Equal(0, await ScalarAsync<int>("SELECT failed_login_count FROM public.users WHERE id=@id", ("id", local.Id)));
        Assert.Equal(1, await ScalarAsync<int>("SELECT lockout_count FROM public.users WHERE id=@id", ("id", local.Id)));
        Assert.Equal(Now.AddMinutes(1), (await _store.FindByIdAsync(local.Id, default))!.LockedUntil);
        AssertFailure(await _store.CreateSessionAsync(local.Id, Session(Now.AddSeconds(59)), default));
        await _store.RecordLoginFailureAsync(local.Id, Now.AddSeconds(1), default);
        Assert.Equal(0, await ScalarAsync<int>("SELECT failed_login_count FROM public.users WHERE id=@id", ("id", local.Id)));
        AssertSuccess(await _store.CreateSessionAsync(local.Id, Session(Now.AddMinutes(1)), default));
        Assert.Null((await _store.FindByIdAsync(local.Id, default))!.LockedUntil);
        Assert.Equal(0, await ScalarAsync<int>("SELECT lockout_count FROM public.users WHERE id=@id", ("id", local.Id)));
    }

    [Fact]
    public async Task RepeatedLockoutsIncreaseDurationToFifteenMinuteCap()
    {
        var local = await LocalAsync();
        var attemptTime = Now;
        foreach (var expectedMinutes in new[] { 1, 2, 4, 8, 15, 15 })
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await _store.RecordLoginFailureAsync(local.Id, attemptTime, default);
            }

            var expectedEnd = attemptTime.AddMinutes(expectedMinutes);
            Assert.Equal(expectedEnd, (await _store.FindByIdAsync(local.Id, default))!.LockedUntil);
            attemptTime = expectedEnd;
        }
    }

    [Fact]
    public async Task MigrationReapplicationPreservesDataAndIsRecordedExactlyOnce()
    {
        var local = await LocalAsync();
        var identity = Google(local.Email);
        AssertSuccess(await _store.LinkGoogleAsync(local.Id, PasswordHash, identity, Now, default));
        AssertSuccess(await _store.CreateSessionAsync(local.Id, Session(), default));
        await Task.WhenAll(IdentitySchemaMigrator.ApplyAsync(database.ConnectionString), IdentitySchemaMigrator.ApplyAsync(database.ConnectionString));
        Assert.Equal(2L, await ScalarAsync<long>("SELECT COUNT(*) FROM public.schema_migrations"));
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.schema_migrations WHERE version=@version", ("version", IdentitySchemaMigrator.CurrentVersion)));
        Assert.True(await _registration.IsReadyAsync(default));
        Assert.NotNull(await _store.FindByIdAsync(local.Id, default));
        Assert.Equal(1L, await CountAsync("external_identities", "user_id", local.Id));
        Assert.Equal(1L, await CountAsync("refresh_sessions", "user_id", local.Id));
    }

    [Fact]
    public async Task ApplicationRoleCanUseRequiredTablesWithoutDdlOrRoleAssignmentPrivileges()
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SET LOCAL ROLE identity_app", connection, transaction);
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT COUNT(*) FROM public.refresh_families";
        Assert.IsType<long>(await command.ExecuteScalarAsync());
        command.CommandText = "SELECT has_table_privilege(current_user, 'public.roles', 'INSERT')";
        Assert.False((bool)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT has_schema_privilege(current_user, 'public', 'CREATE')";
        Assert.False((bool)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT has_table_privilege(current_user, 'public.schema_migrations', 'UPDATE')";
        Assert.False((bool)(await command.ExecuteScalarAsync())!);
    }

    private async Task<RegisteredProfile> LocalAsync()
    {
        var email = NewEmail();
        return Assert.IsType<RegisteredProfile>(
            await _registration.RegisterAsync(email, Normalize(email), "Persistence Customer", PasswordHash, default));
    }

    private static string NewEmail() => Guid.NewGuid().ToString("N") + "@example.test";
    private static string Normalize(string email) => email.Trim().ToUpperInvariant();
    private static VerifiedGoogleIdentity Google(string? email = null) =>
        new(Guid.NewGuid().ToString("N"), email ?? NewEmail(), "Google Customer", null);
    private static RefreshSessionWrite Session(DateTimeOffset? now = null) =>
        new(Guid.NewGuid(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), now ?? Now, (now ?? Now).AddDays(30));

    private static IdentityAccount AssertSuccess(IdentityStoreResult result)
    {
        Assert.Null(result.Failure);
        return Assert.IsType<IdentityAccount>(result.Account);
    }

    private static void AssertFailure(IdentityStoreResult result, IdentityFailure expected = IdentityFailure.InvalidCredentials)
    {
        Assert.Null(result.Account);
        Assert.Equal(expected, result.Failure);
    }

    private async Task<long> CountAsync(string table, string column, object value) =>
        await ScalarAsync<long>($"SELECT COUNT(*) FROM public.{table} WHERE {column}=@value", ("value", value));

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        AddParameters(command, parameters);
        return Assert.IsType<T>(await command.ExecuteScalarAsync());
    }

    private async Task<DateTimeOffset> TimestampAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddParameters(NpgsqlCommand command, IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    private async Task AssertSqlStateAsync(string state, string sql, params (string Name, object Value)[] parameters)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql, parameters));
        Assert.Equal(state, exception.SqlState);
    }

    private async Task WithRejectedAuditAsync(string action, Func<Task> assertion)
    {
        // Action is supplied only by tests, never by an HTTP request or environment.
        Assert.Contains(action, new[] { "UserRegistered", "GoogleLinked" });
        await ExecuteAsync($"""
            CREATE FUNCTION public.identity_test_reject_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.action = '{action}' THEN
                    RAISE EXCEPTION 'Deliberate test audit rejection';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER identity_test_reject_audit BEFORE INSERT ON public.audit_logs
            FOR EACH ROW EXECUTE FUNCTION public.identity_test_reject_audit();
            """);
        try
        {
            await assertion();
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER identity_test_reject_audit ON public.audit_logs; DROP FUNCTION public.identity_test_reject_audit()");
        }
    }

    private static async Task<T[]> ConcurrentAsync<T>(int count, Func<int, Task<T>> action)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count).Select(async index =>
        {
            await start.Task;
            return await action(index);
        }).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }
}

public sealed class IdentityMigrationGuardTests
{
    [Theory]
    [InlineData("swiftdrop_delivery")]
    [InlineData("swiftdrop_merchant")]
    [InlineData("postgres")]
    [InlineData("SWIFTDROP_IDENTITY")]
    [InlineData("")]
    [InlineData(null)]
    public async Task MigrationRejectsForeignOrDefaultDatabaseBeforeOpeningConnection(string? database)
    {
        // No PostgreSQL listener exists at this endpoint. A connection attempt
        // would fail with NpgsqlException rather than the required guard error.
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 1,
            Username = "identity_migration_guard_test",
            Timeout = 1,
            Pooling = false
        };
        if (database is not null)
        {
            connection.Database = database;
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IdentitySchemaMigrator.ApplyAsync(connection.ConnectionString));
        Assert.Equal("Identity migrations require the swiftdrop_identity database.", exception.Message);
    }
}
