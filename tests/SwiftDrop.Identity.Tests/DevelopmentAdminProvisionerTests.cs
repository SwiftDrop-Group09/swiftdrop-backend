using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using Npgsql;

using SwiftDrop.Identity.Api.Infrastructure;
using SwiftDrop.Identity.Api.Infrastructure.Authentication;
using SwiftDrop.Identity.Tests.Persistence;

namespace SwiftDrop.Identity.Tests;

[Collection(IdentityPostgresCollection.Name)]
public sealed class DevelopmentAdminProvisionerTests(IdentityPostgresFixture database)
{
    private const string FictionalPassword = "Fictional Provision Password 7!";

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public async Task NonDevelopmentEnvironmentsAreRejectedBeforeConnecting(string environment)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => DevelopmentAdminProvisioner.ProvisionAsync(
            Configuration(connectionString: "Host=127.0.0.1;Port=1;Database=swiftdrop_identity;Username=fictional"),
            Environment(environment)));
        Assert.Contains("only", exception.Message);
        Assert.DoesNotContain(FictionalPassword, exception.ToString());
    }

    [Theory]
    [InlineData("Identity:DevelopmentAdmin:Email", null)]
    [InlineData("Identity:DevelopmentAdmin:Email", "not-an-email")]
    [InlineData("Identity:DevelopmentAdmin:FullName", null)]
    [InlineData("Identity:DevelopmentAdmin:FullName", "   ")]
    [InlineData("Identity:DevelopmentAdmin:Password", null)]
    [InlineData("Identity:DevelopmentAdmin:Password", "short")]
    public async Task MissingOrInvalidExternalInputsAreRejectedBeforeConnecting(string key, string? value)
    {
        var configuration = Configuration(connectionString:
            "Host=127.0.0.1;Port=1;Database=swiftdrop_identity;Username=fictional", replacement: (key, value));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevelopmentAdminProvisioner.ProvisionAsync(configuration, Environment()));
        Assert.Contains("required", exception.Message);
        Assert.DoesNotContain(FictionalPassword, exception.ToString());
        Assert.DoesNotContain("not-an-email", exception.ToString());
    }

    [Theory]
    [InlineData("Identity:DevelopmentAdmin:Email", 255)]
    [InlineData("Identity:DevelopmentAdmin:FullName", 121)]
    [InlineData("Identity:DevelopmentAdmin:Password", 129)]
    public async Task OversizedInputsAreRejected(string key, int length)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => DevelopmentAdminProvisioner.ProvisionAsync(
            Configuration(replacement: (key, new string('x', length))), Environment()));
        Assert.Contains("required", exception.Message);
    }

    [Theory]
    [InlineData("swiftdrop_delivery")]
    [InlineData("swiftdrop_merchant")]
    [InlineData("SWIFTDROP_IDENTITY")]
    [InlineData("postgres")]
    [InlineData("")]
    public async Task ForeignAndDefaultDatabasesAreRejectedBeforeConnecting(string target)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => DevelopmentAdminProvisioner.ProvisionAsync(
            Configuration(connectionString: $"Host=127.0.0.1;Port=1;Database={target};Username=fictional"), Environment()));
        Assert.Contains("requires the swiftdrop_identity database", exception.Message);
    }

    [Fact]
    public async Task MissingOrMalformedConnectionConfigurationIsRejectedSafely()
    {
        foreach (var connectionString in new[] { "", "NotAConnectionSetting=confidential-value" })
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DevelopmentAdminProvisioner.ProvisionAsync(Configuration(connectionString: connectionString), Environment()));
            Assert.DoesNotContain("confidential-value", exception.ToString());
            Assert.DoesNotContain(FictionalPassword, exception.ToString());
        }
    }

    [Fact]
    public async Task ProvisioningCreatesNewActiveAdministratorWithArgonHashAndSafeAudit()
    {
        var email = NewEmail();
        var configuration = Configuration(email: " " + email + " ");
        await DevelopmentAdminProvisioner.ProvisionAsync(configuration, Environment());
        var account = Assert.IsType<SwiftDrop.Identity.Api.Application.IdentityAccount>(
            await new PostgresIdentityAuthenticationStore(database.DataSource).FindByEmailAsync(email.ToUpperInvariant(), default));
        Assert.NotEqual(Guid.Empty, account.Profile.Id);
        Assert.Equal(email, account.Profile.Email);
        Assert.True(account.Profile.IsActive);
        Assert.Equal(["Admin", "Customer"], account.Profile.Roles);
        Assert.Equal("Development Fixture Administrator", account.Profile.FullName);
        Assert.NotNull(account.PasswordHash);
        Assert.NotEqual(FictionalPassword, account.PasswordHash);
        Assert.StartsWith("argon2id$v=19$m=65536,t=3,p=1$", account.PasswordHash);
        Assert.True(await new Argon2PasswordHasher().VerifyAsync(FictionalPassword, account.PasswordHash, default));
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM public.audit_logs WHERE subject_user_id=@id AND actor_user_id IS NULL
                AND action='UserCreated' AND result_code='Success' AND length(correlation_id)=32
            """, ("id", account.Profile.Id)));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_sessions WHERE user_id=@id", ("id", account.Profile.Id)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingCustomerCannotBePromotedReactivatedOrHavePasswordReplaced(bool isActive)
    {
        var email = NewEmail();
        var account = Assert.IsType<SwiftDrop.Identity.Api.Application.RegisteredProfile>(
            await new PostgresUserRegistrationStore(database.DataSource).RegisterAsync(email, email.ToUpperInvariant(),
                "Preserved Customer", "preserved-existing-password-hash", default));
        if (!isActive) await ExecuteAsync("UPDATE public.users SET is_active=FALSE WHERE id=@id", ("id", account.Id));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevelopmentAdminProvisioner.ProvisionAsync(Configuration(email.ToUpperInvariant()), Environment()));
        Assert.Contains("refuses existing accounts", exception.Message);
        var unchanged = (await new PostgresIdentityAuthenticationStore(database.DataSource).FindByIdAsync(account.Id, default))!;
        Assert.Equal(["Customer"], unchanged.Profile.Roles);
        Assert.Equal(isActive, unchanged.Profile.IsActive);
        Assert.Equal("preserved-existing-password-hash", unchanged.PasswordHash);
        Assert.Equal("Preserved Customer", unchanged.Profile.FullName);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM public.audit_logs WHERE subject_user_id=@id", ("id", account.Id)));
    }

    [Fact]
    public async Task RepeatedProvisioningNeverResetsAnExistingAdministrator()
    {
        var email = NewEmail();
        var configuration = Configuration(email);
        await DevelopmentAdminProvisioner.ProvisionAsync(configuration, Environment());
        var before = (await new PostgresIdentityAuthenticationStore(database.DataSource).FindByEmailAsync(email.ToUpperInvariant(), default))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => DevelopmentAdminProvisioner.ProvisionAsync(
            Configuration(email, replacement: ("Identity:DevelopmentAdmin:Password", "Other Fictional Password 8!")), Environment()));
        var after = (await new PostgresIdentityAuthenticationStore(database.DataSource).FindByIdAsync(before.Profile.Id, default))!;
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM public.audit_logs WHERE subject_user_id=@id", ("id", before.Profile.Id)));
    }

    [Fact]
    public async Task ConcurrentDuplicateProvisioningCreatesExactlyOneAdministratorAndAudit()
    {
        var email = NewEmail();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Provision()
        {
            await start.Task;
            try
            {
                await DevelopmentAdminProvisioner.ProvisionAsync(Configuration(email), Environment());
                return true;
            }
            catch (InvalidOperationException exception)
            {
                Assert.Contains("refuses existing accounts", exception.Message);
                return false;
            }
        }

        var attempts = new[] { Provision(), Provision() };
        start.SetResult();
        var results = await Task.WhenAll(attempts);
        Assert.Single(results, successful => successful);
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.users WHERE normalized_email=@email", ("email", email.ToUpperInvariant())));
        Assert.Equal(2L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM public.user_roles ur JOIN public.users u ON u.id=ur.user_id
            WHERE u.normalized_email=@email
            """, ("email", email.ToUpperInvariant())));
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM public.audit_logs a JOIN public.users u ON u.id=a.subject_user_id
            WHERE u.normalized_email=@email
            """, ("email", email.ToUpperInvariant())));
    }

    [Fact]
    public async Task ProvisioningRequiresCurrentMigrationAndDoesNotApplyIt()
    {
        var email = NewEmail();
        var applied = await ScalarAsync<DateTime>(
            "SELECT applied_at FROM public.schema_migrations WHERE version=@version", ("version", IdentitySchemaMigrator.CurrentVersion));
        await ExecuteAsync("DELETE FROM public.schema_migrations WHERE version=@version", ("version", IdentitySchemaMigrator.CurrentVersion));
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DevelopmentAdminProvisioner.ProvisionAsync(Configuration(email), Environment()));
            Assert.Contains("migrations", exception.Message);
            Assert.Equal(0L, await ScalarAsync<long>(
                "SELECT COUNT(*) FROM public.users WHERE normalized_email=@email", ("email", email.ToUpperInvariant())));
            Assert.Equal(0L, await ScalarAsync<long>(
                "SELECT COUNT(*) FROM public.schema_migrations WHERE version=@version", ("version", IdentitySchemaMigrator.CurrentVersion)));
        }
        finally
        {
            await ExecuteAsync("INSERT INTO public.schema_migrations (version,applied_at) VALUES (@version,@applied)",
                ("version", IdentitySchemaMigrator.CurrentVersion), ("applied", applied));
        }
    }

    [Fact]
    public async Task AuditFailureRollsBackUserRolesAndCredential()
    {
        var email = NewEmail();
        await ExecuteAsync("""
            CREATE FUNCTION public.identity_development_admin_test_reject_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.action='UserCreated' THEN RAISE EXCEPTION 'Deliberate provisioning audit rejection'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER identity_development_admin_test_reject_audit BEFORE INSERT ON public.audit_logs
            FOR EACH ROW EXECUTE FUNCTION public.identity_development_admin_test_reject_audit();
            """);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DevelopmentAdminProvisioner.ProvisionAsync(Configuration(email), Environment()));
            Assert.Equal("Development Admin provisioning could not be completed.", exception.Message);
            Assert.DoesNotContain(FictionalPassword, exception.ToString());
            Assert.DoesNotContain(email, exception.ToString());
        }
        finally
        {
            await ExecuteAsync("""
                DROP TRIGGER identity_development_admin_test_reject_audit ON public.audit_logs;
                DROP FUNCTION public.identity_development_admin_test_reject_audit();
                """);
        }

        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.users WHERE normalized_email=@email", ("email", email.ToUpperInvariant())));
        Assert.Equal(0L, await ScalarAsync<long>("""
            SELECT COUNT(*) FROM public.user_roles ur LEFT JOIN public.users u ON u.id=ur.user_id WHERE u.id IS NULL
            """));
    }

    [Fact]
    public async Task RuntimeApplicationGrantsAreSufficientWithoutMigrationOrSchemaPrivileges()
    {
        var email = NewEmail();
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=identity_app" };
        await DevelopmentAdminProvisioner.ProvisionAsync(Configuration(email, settings.ConnectionString), Environment());
        var account = (await new PostgresIdentityAuthenticationStore(database.DataSource).FindByEmailAsync(email.ToUpperInvariant(), default))!;
        Assert.Equal(["Admin", "Customer"], account.Profile.Roles);
    }

    [Fact]
    public async Task CancellationBeforeProvisioningMakesNoDatabaseChanges()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var email = NewEmail();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DevelopmentAdminProvisioner.ProvisionAsync(
            Configuration(email), Environment(), cancellation.Token));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.users WHERE normalized_email=@email", ("email", email.ToUpperInvariant())));
    }

    private IConfiguration Configuration(string? email = null, string? connectionString = null,
        (string Key, string? Value)? replacement = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = connectionString ?? database.ConnectionString,
            ["Identity:DevelopmentAdmin:Email"] = email ?? NewEmail(),
            ["Identity:DevelopmentAdmin:FullName"] = " Development Fixture Administrator ",
            ["Identity:DevelopmentAdmin:Password"] = FictionalPassword
        };
        if (replacement is { } change) values[change.Key] = change.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

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

    private static string NewEmail() => Guid.NewGuid().ToString("N") + "@example.test";
    private static IHostEnvironment Environment(string name = "Development") => new TestEnvironment { EnvironmentName = name };

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = nameof(DevelopmentAdminProvisionerTests);
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
