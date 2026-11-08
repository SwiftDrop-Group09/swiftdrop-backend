using System.Security.Cryptography;

using Npgsql;

using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Api.Infrastructure;

namespace SwiftDrop.Identity.Tests.Persistence;

[Collection(IdentityPostgresCollection.Name)]
public sealed class AdministrationPersistenceTests(IdentityPostgresFixture database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
    private const string PasswordHash = "test-only-prehashed-administration-password";
    private readonly PostgresIdentityAdministrationStore _store = new(database.DataSource);
    private readonly PostgresIdentityAuthenticationStore _authentication = new(database.DataSource);
    private readonly PostgresUserRegistrationStore _registration = new(database.DataSource);

    [Fact]
    public async Task SelfProfileUpdatePreservesOwnershipEmailRolesAndSafeAudit()
    {
        var user = await LocalAsync();
        var updated = Success(await _store.UpdateProfileAsync(user.Id,
            new ProfileUpdate("Updated Customer", "+94110000000", "Synthetic address"), Now, "profile-test", default));
        Assert.Equal(user.Id, updated.Id);
        Assert.Equal(user.Email, updated.Email);
        Assert.Equal(["Customer"], updated.Roles);
        Assert.Equal("Updated Customer", updated.FullName);
        Assert.Equal("+94110000000", updated.PhoneNumber);
        Assert.Equal("Synthetic address", updated.Address);
        Assert.Equal(updated, Success(await _store.GetProfileAsync(user.Id, default)) with { Roles = updated.Roles });
        Assert.Equal(Now.UtcDateTime, await ScalarAsync<DateTime>(
            "SELECT updated_at FROM public.users WHERE id=@id", ("id", user.Id)));
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM public.audit_logs WHERE actor_user_id=@id AND subject_user_id=@id
                AND action='ProfileUpdated' AND result_code='Success' AND correlation_id='profile-test'
            """, ("id", user.Id)));
    }

    [Fact]
    public async Task DisabledOrMissingProfileCannotBeReadOrUpdated()
    {
        var user = await LocalAsync();
        await ExecuteAsync("UPDATE public.users SET is_active=FALSE WHERE id=@id", ("id", user.Id));
        Assert.Equal(IdentityAdministrationFailure.Forbidden, (await _store.GetProfileAsync(user.Id, default)).Failure);
        Assert.Equal(IdentityAdministrationFailure.Forbidden, (await _store.GetProfileAsync(Guid.NewGuid(), default)).Failure);
        Assert.Equal(IdentityAdministrationFailure.Forbidden, (await _store.UpdateProfileAsync(user.Id,
            new ProfileUpdate("Forbidden", null, null), Now, null, default)).Failure);
        Assert.Equal(user.FullName, (await _authentication.FindByIdAsync(user.Id, default))!.Profile.FullName);
    }

    [Fact]
    public async Task CreateUserHasCustomerOnlyCredentialHashAndTransactionalAudit()
    {
        var admin = await AdministratorAsync();
        var email = NewEmail();
        var created = Success(await _store.CreateUserAsync(admin.Id, email, Normalize(email), "Created User", true,
            PasswordHash, Now, "create-test", default));
        Assert.Equal(["Customer"], created.Roles);
        Assert.True(created.IsActive);
        Assert.Equal(PasswordHash, (await _authentication.FindByIdAsync(created.Id, default))!.PasswordHash);
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM public.audit_logs WHERE actor_user_id=@actor AND subject_user_id=@target
                AND action='UserCreated' AND correlation_id='create-test'
            """, ("actor", admin.Id), ("target", created.Id)));
        Assert.Equal(IdentityAdministrationFailure.Conflict, (await _store.CreateUserAsync(admin.Id,
            email.ToUpperInvariant(), Normalize(email), "Collision", true, PasswordHash, Now, null, default)).Failure);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM public.users WHERE normalized_email=@email",
            ("email", Normalize(email))));
    }

    [Fact]
    public async Task StaleDemotedOrDisabledAdministratorCannotWriteOrReadLists()
    {
        var administrator = await AdministratorAsync();
        var other = await AdministratorAsync();
        var target = await LocalAsync();
        Success(await _store.SetRolesAsync(other.Id, administrator.Id, ["Customer"], Now, null, default));
        await AssertDeniedEveryAdministrationOperationAsync(administrator.Id, target);
        await ExecuteAsync("INSERT INTO public.user_roles (user_id,role_name) VALUES (@id,'Admin')", ("id", administrator.Id));
        await ExecuteAsync("UPDATE public.users SET is_active=FALSE WHERE id=@id", ("id", administrator.Id));
        await AssertDeniedEveryAdministrationOperationAsync(administrator.Id, target);
    }

    [Fact]
    public async Task MissingTargetsAreNotFoundOnlyAfterAuthorization()
    {
        var administrator = await AdministratorAsync();
        var target = Guid.NewGuid();
        Assert.Equal(IdentityAdministrationFailure.NotFound, (await _store.UpdateUserAsync(administrator.Id, target,
            NewEmail(), NewEmail(), "Missing", true, Now, null, default)).Failure);
        Assert.Equal(IdentityAdministrationFailure.NotFound,
            (await _store.SetRolesAsync(administrator.Id, target, ["Customer"], Now, null, default)).Failure);
        Assert.Equal(IdentityAdministrationFailure.NotFound,
            (await _store.DeactivateUserAsync(administrator.Id, target, Now, null, default)).Failure);
        Assert.Equal(IdentityAdministrationFailure.Forbidden,
            (await _store.DeactivateUserAsync(Guid.NewGuid(), target, Now, null, default)).Failure);
    }

    [Fact]
    public async Task AdministratorUpdatePreservesCredentialsAndProviderSubject()
    {
        var administrator = await AdministratorAsync();
        var user = await LocalAsync();
        var subject = Guid.NewGuid().ToString("N");
        await ExecuteAsync("""
            INSERT INTO public.external_identities (id,user_id,provider,subject)
            VALUES (@id,@user,'google',@subject)
            """, ("id", Guid.NewGuid()), ("user", user.Id), ("subject", subject));
        var email = NewEmail();
        var updated = Success(await _store.UpdateUserAsync(administrator.Id, user.Id, email,
            Normalize(email), "Admin Updated", true, Now, null, default));
        Assert.Equal(email, updated.Email);
        Assert.Equal("Admin Updated", updated.FullName);
        Assert.Equal(["Customer"], updated.Roles);
        Assert.Equal(PasswordHash, (await _authentication.FindByIdAsync(user.Id, default))!.PasswordHash);
        Assert.Equal(subject, await ScalarAsync<string>(
            "SELECT subject FROM public.external_identities WHERE user_id=@id", ("id", user.Id)));
        Assert.Equal(Normalize(email), await ScalarAsync<string>(
            "SELECT normalized_email FROM public.users WHERE id=@id", ("id", user.Id)));
    }

    [Fact]
    public async Task ConflictingAdministratorUpdateRollsBackProfileStatusSessionsAndAudit()
    {
        var administrator = await AdministratorAsync();
        var user = await LocalAsync();
        var duplicate = await LocalAsync();
        var session = Session();
        Assert.Null((await _authentication.CreateSessionAsync(user.Id, session, default)).Failure);
        var before = await AuditCountAsync(user.Id);
        Assert.Equal(IdentityAdministrationFailure.Conflict, (await _store.UpdateUserAsync(administrator.Id, user.Id,
            duplicate.Email, Normalize(duplicate.Email), "Rejected", false, Now, null, default)).Failure);
        var unchanged = (await _authentication.FindByIdAsync(user.Id, default))!.Profile;
        Assert.Equal(user.Email, unchanged.Email);
        Assert.Equal(user.FullName, unchanged.FullName);
        Assert.True(unchanged.IsActive);
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_families WHERE user_id=@id AND revoked_at IS NOT NULL", ("id", user.Id)));
        Assert.Equal(before, await AuditCountAsync(user.Id));
    }

    [Theory]
    [InlineData("Customer", "Customer")]
    [InlineData("Unknown", "Customer")]
    [InlineData("admin", "Customer")]
    [InlineData("", "Admin")]
    public async Task InvalidRoleSetsDoNotChangeRolesOrAudit(string first, string second)
    {
        var administrator = await AdministratorAsync();
        var user = await LocalAsync();
        var before = await AuditCountAsync(user.Id);
        Assert.Equal(IdentityAdministrationFailure.InvalidRoles,
            (await _store.SetRolesAsync(administrator.Id, user.Id, [first, second], Now, null, default)).Failure);
        Assert.Equal(["Customer"], (await _authentication.FindByIdAsync(user.Id, default))!.Profile.Roles);
        Assert.Equal(before, await AuditCountAsync(user.Id));
    }

    [Fact]
    public async Task RoleReplacementIsCompleteAtomicAndAllowsNoPrivileges()
    {
        var administrator = await AdministratorAsync();
        var user = await LocalAsync();
        var updated = Success(await _store.SetRolesAsync(administrator.Id, user.Id,
            ["Courier", "Dispatcher"], Now, null, default));
        Assert.Equal(["Courier", "Dispatcher"], updated.Roles);
        var noRoles = Success(await _store.SetRolesAsync(administrator.Id, user.Id, [], Now, null, default));
        Assert.Empty(noRoles.Roles);
        Assert.True(noRoles.IsActive);
        Assert.Equal(2L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.audit_logs WHERE subject_user_id=@id AND action='RolesChanged'", ("id", user.Id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeactivationRevokesEveryFamilyAndPreservesIdentityHistory(bool useUpdate)
    {
        var administrator = await AdministratorAsync();
        var user = await LocalAsync();
        var first = Session();
        var second = Session();
        Assert.Null((await _authentication.CreateSessionAsync(user.Id, first, default)).Failure);
        Assert.Null((await _authentication.CreateSessionAsync(user.Id, second, default)).Failure);
        if (useUpdate)
        {
            Assert.False(Success(await _store.UpdateUserAsync(administrator.Id, user.Id, user.Email,
                Normalize(user.Email), user.FullName, false, Now, null, default)).IsActive);
        }
        else
        {
            Assert.True(Success(await _store.DeactivateUserAsync(administrator.Id, user.Id, Now, null, default)));
        }

        Assert.Equal(2L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_families WHERE user_id=@id AND revoked_at IS NOT NULL", ("id", user.Id)));
        Assert.Equal(2L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_sessions WHERE user_id=@id AND revoked_at IS NOT NULL", ("id", user.Id)));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM public.users WHERE id=@id", ("id", user.Id)));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM public.user_roles WHERE user_id=@id", ("id", user.Id)));
        Assert.True(await AuditCountAsync(user.Id) >= 2);
        Assert.Equal(IdentityFailure.InvalidCredentials, (await _authentication.CreateSessionAsync(user.Id, Session(), default)).Failure);
        Assert.Equal(IdentityFailure.InvalidCredentials,
            (await _authentication.RotateAsync(first.TokenHash, Session(), default)).Failure);
    }

    [Fact]
    public async Task LastActiveAdministratorCannotBeDisabledOrDemotedThroughAnyWrite()
    {
        var administrator = await AdministratorAsync();
        await WithOnlyActiveAdministratorsAsync([administrator.Id], async () =>
        {
            var before = await AuditCountAsync(administrator.Id);
            Assert.Equal(IdentityAdministrationFailure.LastAdmin,
                (await _store.DeactivateUserAsync(administrator.Id, administrator.Id, Now, null, default)).Failure);
            Assert.Equal(IdentityAdministrationFailure.LastAdmin,
                (await _store.SetRolesAsync(administrator.Id, administrator.Id, ["Customer"], Now, null, default)).Failure);
            Assert.Equal(IdentityAdministrationFailure.LastAdmin, (await _store.UpdateUserAsync(administrator.Id,
                administrator.Id, administrator.Email, Normalize(administrator.Email), administrator.FullName,
                false, Now, null, default)).Failure);
            Assert.Equal(before, await AuditCountAsync(administrator.Id));
        });
    }

    [Fact]
    public async Task DeactivationRacingRefreshCannotLeaveALiveReplacementFamily()
    {
        var administrator = await AdministratorAsync();
        var user = await LocalAsync();
        var original = Session();
        Assert.Null((await _authentication.CreateSessionAsync(user.Id, original, default)).Failure);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IdentityStoreResult> Refresh()
        {
            await start.Task;
            return await _authentication.RotateAsync(original.TokenHash, Session(), default);
        }

        async Task<IdentityAdministrationResult<bool>> Deactivate()
        {
            await start.Task;
            return await _store.DeactivateUserAsync(administrator.Id, user.Id, Now, null, default);
        }

        var refresh = Refresh();
        var deactivate = Deactivate();
        start.SetResult();
        await Task.WhenAll(refresh, deactivate);
        Assert.Null((await deactivate).Failure);
        Assert.Contains((await refresh).Failure, new IdentityFailure?[] { null, IdentityFailure.InvalidCredentials });
        Assert.False((await _authentication.FindByIdAsync(user.Id, default))!.Profile.IsActive);
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_families WHERE user_id=@id AND revoked_at IS NULL", ("id", user.Id)));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_sessions WHERE user_id=@id AND revoked_at IS NULL", ("id", user.Id)));
    }

    [Fact]
    public async Task ConcurrentSelfDemotionsLeaveExactlyOneActiveAdministrator()
    {
        var first = await AdministratorAsync();
        var second = await AdministratorAsync();
        await WithOnlyActiveAdministratorsAsync([first.Id, second.Id], async () =>
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<IdentityAdministrationResult<RegisteredProfile>> Demote(Guid actor)
            {
                await start.Task;
                return await _store.SetRolesAsync(actor, actor, ["Customer"], Now, null, default);
            }

            var operations = new[] { Demote(first.Id), Demote(second.Id) };
            start.SetResult();
            var results = await Task.WhenAll(operations);
            Assert.Single(results, result => result.Failure is null);
            Assert.Single(results, result => result.Failure == IdentityAdministrationFailure.LastAdmin);
            Assert.Equal(1L, await ActiveAdministratorCountAsync());
        });
    }

    [Fact]
    public async Task ConcurrentDeactivationAndRoleRemovalShareTheSameLastAdministratorGuard()
    {
        var first = await AdministratorAsync();
        var second = await AdministratorAsync();
        await WithOnlyActiveAdministratorsAsync([first.Id, second.Id], async () =>
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<IdentityAdministrationFailure?> Disable()
            {
                await start.Task;
                return (await _store.DeactivateUserAsync(first.Id, first.Id, Now, null, default)).Failure;
            }

            async Task<IdentityAdministrationFailure?> Demote()
            {
                await start.Task;
                return (await _store.SetRolesAsync(second.Id, second.Id, [], Now, null, default)).Failure;
            }

            var operations = new[] { Disable(), Demote() };
            start.SetResult();
            var failures = await Task.WhenAll(operations);
            Assert.Single(failures, failure => failure is null);
            Assert.Single(failures, failure => failure == IdentityAdministrationFailure.LastAdmin);
            Assert.Equal(1L, await ActiveAdministratorCountAsync());
        });
    }

    [Theory]
    [InlineData("ProfileUpdated")]
    [InlineData("UserCreated")]
    [InlineData("UserUpdated")]
    [InlineData("RolesChanged")]
    [InlineData("UserDeactivated")]
    public async Task AuditFailureRollsBackEveryAdministrativeMutation(string action)
    {
        var administrator = await AdministratorAsync();
        var user = await LocalAsync();
        var email = NewEmail();
        Assert.Null((await _authentication.CreateSessionAsync(user.Id, Session(), default)).Failure);
        var before = await AuditCountAsync(user.Id);
        await WithRejectedAuditAsync(action, async () =>
        {
            await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                switch (action)
                {
                    case "ProfileUpdated":
                        await _store.UpdateProfileAsync(user.Id, new ProfileUpdate("Rejected", "phone", "address"), Now, null, default);
                        break;
                    case "UserCreated":
                        await _store.CreateUserAsync(administrator.Id, email, Normalize(email), "Rejected", true, PasswordHash, Now, null, default);
                        break;
                    case "UserUpdated":
                        await _store.UpdateUserAsync(administrator.Id, user.Id, user.Email, Normalize(user.Email), "Rejected", false, Now, null, default);
                        break;
                    case "RolesChanged":
                        await _store.SetRolesAsync(administrator.Id, user.Id, ["Admin"], Now, null, default);
                        break;
                    case "UserDeactivated":
                        await _store.DeactivateUserAsync(administrator.Id, user.Id, Now, null, default);
                        break;
                }
            });
        });
        var unchanged = (await _authentication.FindByIdAsync(user.Id, default))!.Profile;
        Assert.True(unchanged.IsActive);
        Assert.Equal(user.FullName, unchanged.FullName);
        Assert.Equal(["Customer"], unchanged.Roles);
        Assert.Null(unchanged.PhoneNumber);
        Assert.Equal(before, await AuditCountAsync(user.Id));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM public.users WHERE normalized_email=@email", ("email", Normalize(email))));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_families WHERE user_id=@id AND revoked_at IS NOT NULL", ("id", user.Id)));
    }

    [Fact]
    public async Task ListsArePaginatedStableAndAuditProjectionExcludesPrivateMetadata()
    {
        var administrator = await AdministratorAsync();
        var user = await LocalAsync();
        Success(await _store.UpdateProfileAsync(user.Id, new ProfileUpdate("List User", null, null), Now, "internal-correlation", default));
        var first = Success(await _store.ListUsersAsync(administrator.Id, 1, 2, default));
        var repeat = Success(await _store.ListUsersAsync(administrator.Id, 1, 2, default));
        var second = Success(await _store.ListUsersAsync(administrator.Id, 2, 2, default));
        Assert.Equal(first.Items.Select(item => item.Id), repeat.Items.Select(item => item.Id));
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
        Assert.Equal(first.TotalCount, second.TotalCount);
        Assert.Equal(await ScalarAsync<long>("SELECT COUNT(*) FROM public.users"), first.TotalCount);
        Assert.Equal(2, first.PageSize);
        Assert.Empty(Success(await _store.ListUsersAsync(administrator.Id, int.MaxValue, 100, default)).Items);
        var audit = Success(await _store.ListAuditAsync(administrator.Id, 1, 100, default));
        Assert.Equal(await ScalarAsync<long>("SELECT COUNT(*) FROM public.audit_logs"), audit.TotalCount);
        Assert.All(audit.Items, item => Assert.Matches("^[A-Za-z][A-Za-z0-9_]{0,79}$", item.ResultCode));
        Assert.Equal(["Id", "ActorId", "Action", "ResourceId", "Timestamp", "ResultCode"],
            typeof(IdentityAuditRecord).GetProperties().Select(property => property.Name));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task InvalidPaginationCannotReachDatabaseQueries(int page, int pageSize)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.ListUsersAsync(Guid.NewGuid(), page, pageSize, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.ListAuditAsync(Guid.NewGuid(), page, pageSize, default));
    }

    [Fact]
    public async Task MigrationAllowsRoleReplacementAndKeepsAuditAppendOnlyForApplication()
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SET LOCAL ROLE identity_app", connection, transaction);
        await command.ExecuteNonQueryAsync();
        foreach (var (table, privilege, expected) in new[]
        {
            ("user_roles", "DELETE", true), ("audit_logs", "INSERT", true), ("audit_logs", "UPDATE", false),
            ("audit_logs", "DELETE", false), ("roles", "INSERT", false), ("users", "DELETE", false)
        })
        {
            command.CommandText = "SELECT has_table_privilege(current_user,@table,@privilege)";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("table", "public." + table);
            command.Parameters.AddWithValue("privilege", privilege);
            Assert.Equal(expected, (bool)(await command.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task AdministrationStoreWorksWithRestrictedApplicationGrants()
    {
        var administrator = await AdministratorAsync();
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Options = "-c role=identity_app"
        };
        await using var restrictedSource = NpgsqlDataSource.Create(settings.ConnectionString);
        var restricted = new PostgresIdentityAdministrationStore(restrictedSource);
        var email = NewEmail();
        var user = Success(await restricted.CreateUserAsync(administrator.Id, email, Normalize(email),
            "Restricted role created", true, PasswordHash, Now, null, default));
        Assert.Equal(["Courier"], Success(await restricted.SetRolesAsync(administrator.Id,
            user.Id, ["Courier"], Now, null, default)).Roles);
        Assert.True(Success(await restricted.DeactivateUserAsync(administrator.Id, user.Id, Now, null, default)));
        Assert.NotEmpty(Success(await restricted.ListAuditAsync(administrator.Id, 1, 100, default)).Items);
    }

    [Theory]
    [InlineData("request body contains secrets", null)]
    [InlineData("Success", "untrusted\ncorrelation")]
    [InlineData("", null)]
    public async Task AuditDatabaseRejectsUnstructuredCodesAndUnsafeCorrelation(string resultCode, string? correlation)
    {
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO public.audit_logs (action,result_code,correlation_id)
            VALUES ('ConstraintTest',@result,@correlation)
            """);
        command.Parameters.AddWithValue("result", resultCode);
        command.Parameters.Add("correlation", NpgsqlTypes.NpgsqlDbType.Varchar).Value = (object?)correlation ?? DBNull.Value;
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    private async Task AssertDeniedEveryAdministrationOperationAsync(Guid actor, RegisteredProfile target)
    {
        Assert.Equal(IdentityAdministrationFailure.Forbidden, (await _store.ListUsersAsync(actor, 1, 20, default)).Failure);
        Assert.Equal(IdentityAdministrationFailure.Forbidden, (await _store.ListAuditAsync(actor, 1, 20, default)).Failure);
        Assert.Equal(IdentityAdministrationFailure.Forbidden, (await _store.CreateUserAsync(actor, NewEmail(), NewEmail(),
            "Forbidden", true, PasswordHash, Now, null, default)).Failure);
        Assert.Equal(IdentityAdministrationFailure.Forbidden, (await _store.UpdateUserAsync(actor, target.Id, target.Email,
            Normalize(target.Email), "Forbidden", false, Now, null, default)).Failure);
        Assert.Equal(IdentityAdministrationFailure.Forbidden,
            (await _store.SetRolesAsync(actor, target.Id, ["Admin"], Now, null, default)).Failure);
        Assert.Equal(IdentityAdministrationFailure.Forbidden,
            (await _store.DeactivateUserAsync(actor, target.Id, Now, null, default)).Failure);
    }

    private async Task<RegisteredProfile> LocalAsync()
    {
        var email = NewEmail();
        return Assert.IsType<RegisteredProfile>(await _registration.RegisterAsync(email, Normalize(email), "Administration Customer", PasswordHash, default));
    }

    private async Task<RegisteredProfile> AdministratorAsync()
    {
        var user = await LocalAsync();
        await ExecuteAsync("INSERT INTO public.user_roles (user_id,role_name) VALUES (@id,'Admin')", ("id", user.Id));
        return user with { Roles = ["Admin", "Customer"] };
    }

    private async Task WithOnlyActiveAdministratorsAsync(Guid[] keep, Func<Task> action)
    {
        var disabled = new List<Guid>();
        await using (var command = database.DataSource.CreateCommand("""
            UPDATE public.users SET is_active=FALSE WHERE is_active AND NOT (id=ANY(@keep))
                AND id IN (SELECT user_id FROM public.user_roles WHERE role_name='Admin') RETURNING id
            """))
        {
            command.Parameters.AddWithValue("keep", keep);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) disabled.Add(reader.GetGuid(0));
        }

        try { await action(); }
        finally { await ExecuteAsync("UPDATE public.users SET is_active=TRUE WHERE id=ANY(@ids)", ("ids", disabled.ToArray())); }
    }

    private Task<long> ActiveAdministratorCountAsync() => ScalarAsync<long>("""
        SELECT COUNT(*) FROM public.users u JOIN public.user_roles ur ON ur.user_id=u.id
        WHERE u.is_active AND ur.role_name='Admin'
        """);

    private Task<long> AuditCountAsync(Guid user) => ScalarAsync<long>(
        "SELECT COUNT(*) FROM public.audit_logs WHERE subject_user_id=@id", ("id", user));

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Assert.IsType<T>(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WithRejectedAuditAsync(string action, Func<Task> assertion)
    {
        Assert.Contains(action, new[] { "ProfileUpdated", "UserCreated", "UserUpdated", "RolesChanged", "UserDeactivated" });
        await ExecuteAsync($"""
            CREATE FUNCTION public.identity_administration_test_reject_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.action = '{action}' THEN RAISE EXCEPTION 'Deliberate administration audit rejection'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER identity_administration_test_reject_audit BEFORE INSERT ON public.audit_logs
            FOR EACH ROW EXECUTE FUNCTION public.identity_administration_test_reject_audit();
            """);
        try { await assertion(); }
        finally
        {
            await ExecuteAsync("""
                DROP TRIGGER identity_administration_test_reject_audit ON public.audit_logs;
                DROP FUNCTION public.identity_administration_test_reject_audit();
                """);
        }
    }

    private static T Success<T>(IdentityAdministrationResult<T> result)
    {
        Assert.Null(result.Failure);
        Assert.NotNull(result.Value);
        return result.Value;
    }

    private static string NewEmail() => Guid.NewGuid().ToString("N") + "@example.test";
    private static string Normalize(string email) => email.ToUpperInvariant();
    private static RefreshSessionWrite Session() => new(Guid.NewGuid(),
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Now, Now.AddDays(30));
}
