using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Api.Infrastructure;
using SwiftDrop.Identity.Tests.Persistence;

namespace SwiftDrop.Identity.Tests;

[Collection(IdentityPostgresCollection.Name)]
public sealed class AdministrationApiTests(IdentityPostgresFixture database)
{
    private const string Password = "Test-only generated password 451!";
    private static readonly Lazy<Task<string>> Hash = new(() => new Argon2PasswordHasher().HashAsync(Password, default));

    [Theory]
    [InlineData("GET", "/api/v1/users/me")]
    [InlineData("PUT", "/api/v1/users/me")]
    [InlineData("GET", "/api/v1/users")]
    [InlineData("POST", "/api/v1/users")]
    [InlineData("PUT", "/api/v1/users/11111111-1111-1111-1111-111111111111")]
    [InlineData("DELETE", "/api/v1/users/11111111-1111-1111-1111-111111111111")]
    [InlineData("PUT", "/api/v1/users/11111111-1111-1111-1111-111111111111/roles")]
    [InlineData("GET", "/api/v1/audit-logs")]
    public async Task AnonymousAndSpoofedRoleHeadersCannotManageAccounts(string method, string path)
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-User-Id", Guid.NewGuid().ToString());
        request.Headers.Add("X-User-Roles", "Admin");
        request.Headers.Add("X-API-Key", "not-a-human-credential");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/v1/users")]
    [InlineData("POST", "/api/v1/users")]
    [InlineData("PUT", "/api/v1/users/11111111-1111-1111-1111-111111111111")]
    [InlineData("DELETE", "/api/v1/users/11111111-1111-1111-1111-111111111111")]
    [InlineData("PUT", "/api/v1/users/11111111-1111-1111-1111-111111111111/roles")]
    [InlineData("GET", "/api/v1/audit-logs")]
    public async Task CustomerCannotInvokeAdminOperations(string method, string path)
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        await Authenticate(client);
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ProfileUpdateUsesVerifiedSubjectAndReturnsOnlySafeFields()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var user = await Authenticate(client);
        using var read = await client.GetAsync("/api/v1/users/me");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(user.Id, (await read.Content.ReadFromJsonAsync<RegisteredProfile>())!.Id);
        using var response = await client.PutAsJsonAsync("/api/v1/users/me", new { fullName = " Updated ", phoneNumber = "0771234567", address = "New address" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = (await response.Content.ReadFromJsonAsync<RegisteredProfile>())!;
        Assert.Equal(user.Id, profile.Id);
        Assert.Equal(user.Email, profile.Email);
        Assert.Equal("Updated", profile.FullName);
        Assert.Equal(["Customer"], profile.Roles);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "address", "email", "fullName", "id", "isActive", "phoneNumber", "roles" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    [Theory]
    [InlineData("roles", "[\"Admin\"]")]
    [InlineData("id", "\"11111111-1111-1111-1111-111111111111\"")]
    [InlineData("email", "\"different@example.test\"")]
    [InlineData("isActive", "false")]
    [InlineData("passwordHash", "\"fake\"")]
    [InlineData("provider", "\"google\"")]
    public async Task SelfProfileRejectsPrivilegedFields(string field, string value)
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var user = await Authenticate(client);
        using var body = new StringContent("{\"fullName\":\"Changed\",\"phoneNumber\":null,\"" + field + "\":" + value + "}", Encoding.UTF8, "application/json");
        using var response = await client.PutAsync("/api/v1/users/me", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(user.FullName, (await client.GetFromJsonAsync<RegisteredProfile>("/api/v1/users/me"))!.FullName);
    }

    [Theory]
    [InlineData("{\"fullName\":\"A\",\"fullName\":\"B\"}")]
    [InlineData("{\"FullName\":\"WrongCase\"}")]
    [InlineData("{\"fullName\":5}")]
    [InlineData("{\"fullName\":\" \"}")]
    [InlineData("{\"fullName\":\"Missing required phoneNumber\"}")]
    public async Task MalformedProfileInputsDoNotMutate(string json)
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        await Authenticate(client);
        using var response = await client.PutAsync("/api/v1/users/me", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminCreationHashesPasswordAndRoleAssignmentIsSeparate()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        await Authenticate(client, admin: true);
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/users", new { email, fullName = "Created", isActive = true, password = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = (await response.Content.ReadFromJsonAsync<RegisteredProfile>())!;
        Assert.Equal(["Customer"], user.Roles);
        Assert.DoesNotContain(Password, await response.Content.ReadAsStringAsync());
        await using var query = database.DataSource.CreateCommand("SELECT password_hash FROM public.users WHERE id=@id");
        query.Parameters.AddWithValue("id", user.Id);
        var hash = (string)(await query.ExecuteScalarAsync())!;
        Assert.NotEqual(Password, hash);
        Assert.True(await new Argon2PasswordHasher().VerifyAsync(Password, hash, default));
        using var roles = await client.PutAsJsonAsync($"/api/v1/users/{user.Id}/roles", new { roles = new[] { "Customer", "Courier" } });
        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
        Assert.Equal(["Courier", "Customer"], (await roles.Content.ReadFromJsonAsync<RegisteredProfile>())!.Roles);
        using var duplicate = await client.PostAsJsonAsync("/api/v1/users", new { email = email.ToUpperInvariant(), fullName = "Duplicate", isActive = true, password = Password });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var rejectedPasswordUpdate = await client.PutAsJsonAsync($"/api/v1/users/{user.Id}", new { email, fullName = "Update", isActive = true, password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, rejectedPasswordUpdate.StatusCode);
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?page=1&page=2")]
    [InlineData("?page=-1")]
    [InlineData("?email=unapproved-filter")]
    public async Task AdminPaginationRejectsUnspecifiedOrInvalidQueries(string query)
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        await Authenticate(client, admin: true);
        using var users = await client.GetAsync("/api/v1/users" + query);
        using var audit = await client.GetAsync("/api/v1/audit-logs" + query);
        Assert.Equal(HttpStatusCode.BadRequest, users.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, audit.StatusCode);
    }

    [Fact]
    public async Task AuditAndUserPagesAreBoundedSafeProjections()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        await Authenticate(client, admin: true);
        using var users = await client.GetAsync("/api/v1/users?page=1&pageSize=2");
        using var audit = await client.GetAsync("/api/v1/audit-logs?page=1&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        using var json = JsonDocument.Parse(await audit.Content.ReadAsStringAsync());
        foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(new[] { "action", "actorId", "id", "resourceId", "resultCode", "timestamp" },
                item.EnumerateObject().Select(property => property.Name).Order().ToArray());
        }
        Assert.DoesNotContain(Password, await audit.Content.ReadAsStringAsync());
        Assert.DoesNotContain("passwordHash", await users.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeactivationRevokesAllFamiliesAndBlocksOldIdentityTokenAtManagementBoundary()
    {
        using var factory = new IdentityApiFactory(database);
        using var admin = factory.Client();
        using var customer = factory.Client();
        await Authenticate(admin, admin: true);
        var user = await Authenticate(customer);
        using var login = await customer.PostAsJsonAsync("/api/v1/auth/login", new { user.Email, password = Password });
        using var tokens = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var refresh = tokens.RootElement.GetProperty("refreshToken").GetString();
        using var disabled = await admin.DeleteAsync($"/api/v1/users/{user.Id}");
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        using var own = await customer.GetAsync("/api/v1/users/me");
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        using var relogin = await customer.PostAsJsonAsync("/api/v1/auth/login", new { user.Email, password = Password });
        using var rotated = await customer.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, relogin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rotated.StatusCode);
        using var enabled = await admin.PutAsJsonAsync($"/api/v1/users/{user.Id}", new { user.Email, user.FullName, isActive = true });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        using var oldRefresh = await customer.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
    }

    [Fact]
    public async Task DemotedAdminCannotUseStaleAdminJwtAndZeroRoleUserCanReadOwnProfile()
    {
        using var factory = new IdentityApiFactory(database);
        using var admin = factory.Client();
        using var target = factory.Client();
        await Authenticate(admin, admin: true);
        var user = await Authenticate(target, admin: true);
        using var removed = await admin.PutAsJsonAsync($"/api/v1/users/{user.Id}/roles", new { roles = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        using var denied = await target.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        target.DefaultRequestHeaders.Authorization = null;
        using var login = await target.PostAsJsonAsync("/api/v1/auth/login", new { user.Email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var pair = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        target.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.RootElement.GetProperty("accessToken").GetString());
        using var own = await target.GetAsync("/api/v1/users/me");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using var forbidden = await target.GetAsync("/api/v1/audit-logs");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task BrowserMutationsRequireCsrfEvenWithValidAdminJwt()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        await Authenticate(client, admin: true);
        client.DefaultRequestHeaders.Add("Origin", IdentityApiFactory.Origin);
        using var profile = await client.PutAsJsonAsync("/api/v1/users/me", new { fullName = "CSRF", phoneNumber = "123" });
        using var create = await client.PostAsJsonAsync("/api/v1/users", new { email = "csrf@example.test", fullName = "CSRF", isActive = true, password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, profile.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task BrowserProfileUpdateWithCsrfStoresSafeCorrelationAndProblemReturnsIt()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client(cookies: true);
        var user = await Authenticate(client);
        using var bootstrap = await client.GetAsync("/api/v1/auth/csrf");
        using var json = JsonDocument.Parse(await bootstrap.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("Origin", IdentityApiFactory.Origin);
        client.DefaultRequestHeaders.Add("X-CSRF-Token", json.RootElement.GetProperty("csrfToken").GetString());
        const string correlation = "aaaaaaaa-1111-2222-3333-123456789abc";
        client.DefaultRequestHeaders.Add("X-Correlation-ID", correlation);
        using var update = await client.PutAsJsonAsync("/api/v1/users/me", new { fullName = "Browser", phoneNumber = "" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(correlation, update.Headers.GetValues("X-Correlation-ID").Single());
        await using var audit = database.DataSource.CreateCommand("SELECT correlation_id FROM public.audit_logs WHERE subject_user_id=@id AND action='ProfileUpdated' ORDER BY id DESC LIMIT 1");
        audit.Parameters.AddWithValue("id", user.Id);
        Assert.Equal(correlation, await audit.ExecuteScalarAsync());
        using var invalid = await client.PutAsJsonAsync("/api/v1/users/me", new { fullName = "" });
        using var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        Assert.Equal(correlation, problem.RootElement.GetProperty("correlationId").GetString());
        client.DefaultRequestHeaders.Remove("X-Correlation-ID");
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "-unsafe-prefix");
        using var safe = await client.PutAsJsonAsync("/api/v1/users/me", new { fullName = "Sanitized", phoneNumber = "" });
        Assert.Equal(HttpStatusCode.OK, safe.StatusCode);
        Assert.NotEqual("-unsafe-prefix", safe.Headers.GetValues("X-Correlation-ID").Single());
    }

    private async Task<RegisteredProfile> Authenticate(HttpClient client, bool admin = false)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        var user = (await new PostgresUserRegistrationStore(database.DataSource)
            .RegisterAsync(email, email.ToUpperInvariant(), "Fixture", await Hash.Value, default))!;
        if (admin)
        {
            await using var command = database.DataSource.CreateCommand("INSERT INTO public.user_roles (user_id,role_name) VALUES (@id,'Admin')");
            command.Parameters.AddWithValue("id", user.Id);
            await command.ExecuteNonQueryAsync();
        }
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.RootElement.GetProperty("accessToken").GetString());
        return user;
    }
}
