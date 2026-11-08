using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Api.Infrastructure;
using SwiftDrop.Identity.Tests.Persistence;

namespace SwiftDrop.Identity.Tests;

[Collection(IdentityPostgresCollection.Name)]
public sealed class GoogleApiTests(IdentityPostgresFixture database)
{
    private const string Password = "Correct Horse Battery 7!";

    [Fact]
    public async Task NewGoogleIdentityCreatesPasswordlessCustomerAndExistingSubjectKeepsSameUser()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = Email();
        var subject = Guid.NewGuid().ToString("N");
        var providerToken = Google(factory, subject, email);

        using var firstResponse = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = providerToken });
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        using var first = await Json(firstResponse);
        var userId = first.RootElement.GetProperty("user").GetProperty("id").GetGuid();
        Assert.Equal("Bearer", first.RootElement.GetProperty("tokenType").GetString());
        Assert.Equal(900, first.RootElement.GetProperty("expiresInSeconds").GetInt32());
        Assert.Equal(43, first.RootElement.GetProperty("refreshToken").GetString()!.Length);
        Assert.Equal(["Customer"], Roles(first.RootElement.GetProperty("user")));
        Assert.True(first.RootElement.GetProperty("user").GetProperty("isActive").GetBoolean());
        Assert.False(first.RootElement.GetProperty("user").TryGetProperty("passwordHash", out _));
        Assert.DoesNotContain(providerToken, first.RootElement.GetRawText());
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM public.users WHERE id=@id AND password_hash IS NULL", userId));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM public.external_identities WHERE user_id=@id AND provider='google'", userId));

        // The verified provider email can change; subject remains the identity key.
        var nextProviderToken = Google(factory, subject, Email());
        using var nextResponse = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = nextProviderToken });
        Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
        using var next = await Json(nextResponse);
        Assert.Equal(userId, next.RootElement.GetProperty("user").GetProperty("id").GetGuid());
        Assert.Equal(email, next.RootElement.GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM public.external_identities WHERE user_id=@id", userId));
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM public.refresh_sessions WHERE user_id=@id", userId));
    }

    [Fact]
    public async Task MatchingLocalEmailRequiresExplicitAuthenticatedPasswordVerifiedLink()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = Email();
        var userId = await Register(client, email);
        var providerToken = Google(factory, Guid.NewGuid().ToString("N"), email.ToUpperInvariant());
        var beforeCollision = await Snapshot();

        using var collision = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = providerToken });
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        using var collisionBody = await Json(collision);
        Assert.Equal("account_link_required", collisionBody.RootElement.GetProperty("code").GetString());
        Assert.Equal(beforeCollision, await Snapshot());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(client, email));
        var beforeLinkSessions = await Scalar("SELECT COUNT(*) FROM public.refresh_sessions WHERE user_id=@id", userId);
        using var linked = await client.PostAsJsonAsync("/api/v1/auth/google/link", new { idToken = providerToken, currentPassword = Password });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        using var linkedBody = await Json(linked);
        Assert.Equal(userId, linkedBody.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(["Customer"], Roles(linkedBody.RootElement));
        Assert.False(linkedBody.RootElement.TryGetProperty("accessToken", out _));
        Assert.False(linkedBody.RootElement.TryGetProperty("refreshToken", out _));
        Assert.False(linkedBody.RootElement.TryGetProperty("passwordHash", out _));
        Assert.Equal(beforeLinkSessions, await Scalar("SELECT COUNT(*) FROM public.refresh_sessions WHERE user_id=@id", userId));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM public.external_identities WHERE user_id=@id", userId));

        using var login = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = providerToken });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginBody = await Json(login);
        Assert.Equal(userId, loginBody.RootElement.GetProperty("user").GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousAndWrongPasswordLinkAttemptsDoNotCreateProviderIdentity(bool authenticated)
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = Email();
        var userId = await Register(client, email);
        if (authenticated)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(client, email));
        }

        var token = Google(factory, Guid.NewGuid().ToString("N"), email);
        var before = await Snapshot();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/google/link", new
        {
            idToken = token, currentPassword = authenticated ? "Wrong Horse Battery 7!" : Password
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var after = await Snapshot();
        Assert.Equal(before.Split(':').Take(4), after.Split(':').Take(4));
        Assert.Equal(long.Parse(before.Split(':')[4]) + (authenticated ? 1 : 0), long.Parse(after.Split(':')[4]));
        Assert.Equal(authenticated ? 1L : 0L, await Scalar("""
            SELECT COUNT(*) FROM public.audit_logs WHERE subject_user_id=@id
                AND action='LocalReauthenticationRejected' AND result_code='Rejected'
            """, userId));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM public.external_identities WHERE user_id=@id", userId));
    }

    [Fact]
    public async Task DifferentEmailLinkIsRejectedWithoutChangingAccountOrSessions()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = Email();
        await Register(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(client, email));
        var token = Google(factory, Guid.NewGuid().ToString("N"), Email());
        var before = await Snapshot();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/google/link", new { idToken = token, currentPassword = Password });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task GoogleSubjectOwnedByAnotherUserCannotBeTransferredByMatchingCurrentEmail()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var subject = Guid.NewGuid().ToString("N");
        var originalToken = Google(factory, subject, Email());
        using var created = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = originalToken });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var createdBody = await Json(created);
        var originalUser = createdBody.RootElement.GetProperty("user").GetProperty("id").GetGuid();

        var localEmail = Email();
        var localUser = await Register(client, localEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(client, localEmail));
        var conflictingToken = Google(factory, subject, localEmail);
        var before = await Snapshot();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/google/link", new { idToken = conflictingToken, currentPassword = Password });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await Snapshot());
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM public.external_identities WHERE user_id=@id", originalUser));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM public.external_identities WHERE user_id=@id", localUser));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledAccountsCannotLoginWithGoogleOrLinkUsingUnexpiredJwt(bool link)
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = Email();
        var token = Google(factory, Guid.NewGuid().ToString("N"), email);
        Guid userId;
        if (link)
        {
            userId = await Register(client, email);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(client, email));
        }
        else
        {
            using var created = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = token });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            using var body = await Json(created);
            userId = body.RootElement.GetProperty("user").GetProperty("id").GetGuid();
        }

        await using (var command = database.DataSource.CreateCommand("UPDATE public.users SET is_active=FALSE WHERE id=@id"))
        {
            command.Parameters.AddWithValue("id", userId);
            await command.ExecuteNonQueryAsync();
        }

        var before = await Snapshot();
        using var response = link
            ? await client.PostAsJsonAsync("/api/v1/auth/google/link", new { idToken = token, currentPassword = Password })
            : await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = token });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task PasswordChangedAfterVerificationPreventsGoogleLinkCommit()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = Email();
        var userId = await Register(client, email);
        var accessToken = await Login(client, email);
        var identity = new VerifiedGoogleIdentity(Guid.NewGuid().ToString("N"), email, "Customer", null);
        var replacementHash = await new Argon2PasswordHasher().HashAsync("Different secure password 8!", default);
        using var changingFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGoogleIdentityVerifier>();
            services.AddSingleton<IGoogleIdentityVerifier>(new PasswordChangeVerifier(async () =>
            {
                await using var command = database.DataSource.CreateCommand("UPDATE public.users SET password_hash=@hash WHERE id=@id");
                command.Parameters.AddWithValue("id", userId);
                command.Parameters.AddWithValue("hash", replacementHash);
                await command.ExecuteNonQueryAsync();
                return identity;
            }));
        }));
        using var changingClient = changingFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(IdentityApiFactory.Origin), HandleCookies = false
        });
        changingClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var before = await Snapshot();

        using var response = await changingClient.PostAsJsonAsync("/api/v1/auth/google/link", new { idToken = "verified-provider-token", currentPassword = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, await Snapshot());
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM public.external_identities WHERE user_id=@id", userId));
    }

    [Theory]
    [InlineData("{}", HttpStatusCode.BadRequest)]
    [InlineData("{\"idToken\":null}", HttpStatusCode.BadRequest)]
    [InlineData("{\"idToken\":\"\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"idToken\":\"invalid-provider-token\"}", HttpStatusCode.Unauthorized)]
    [InlineData("{\"idToken\":\"invalid-provider-token\",\"role\":\"Admin\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"idToken\":\"invalid-provider-token\",\"clientType\":\"mobile\"}", HttpStatusCode.BadRequest)]
    public async Task InvalidOrExtraGoogleInputHasNoAccountProviderOrSessionSideEffects(string json, HttpStatusCode expected)
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var before = await Snapshot();
        using var response = await client.PostAsync("/api/v1/auth/google", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task NativeNonceBearingTokenWithoutServerChallengeFailsClosed()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var token = Google(factory, Guid.NewGuid().ToString("N"), Email(), "unbound-provider-nonce");
        var before = await Snapshot();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = token });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, await Snapshot());
    }

    private static string Google(IdentityApiFactory factory, string subject, string email, string? nonce = null)
    {
        var token = "provider-token-" + Guid.NewGuid().ToString("N");
        Assert.True(factory.Google.Tokens.TryAdd(token, new VerifiedGoogleIdentity(subject, email, "Customer One", nonce)));
        return token;
    }

    private static async Task<Guid> Register(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, fullName = "Customer One" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await Json(response);
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<string> Login(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await Json(response);
        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task<string> Snapshot()
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT (SELECT COUNT(*) FROM public.users)::text || ':' ||
                   (SELECT COUNT(*) FROM public.external_identities)::text || ':' ||
                   (SELECT COUNT(*) FROM public.refresh_sessions)::text || ':' ||
                   (SELECT COUNT(*) FROM public.refresh_families)::text || ':' ||
                   (SELECT COUNT(*) FROM public.audit_logs)::text
            """);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> Scalar(string sql, Guid userId)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", userId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static string[] Roles(JsonElement user) => user.GetProperty("roles").EnumerateArray().Select(role => role.GetString()!).ToArray();
    private static string Email() => "google-api-" + Guid.NewGuid().ToString("N") + "@example.test";
    private static async Task<JsonDocument> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private sealed class PasswordChangeVerifier(Func<Task<VerifiedGoogleIdentity>> verify) : IGoogleIdentityVerifier
    {
        public async Task<VerifiedGoogleIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken) => await verify();
    }
}
