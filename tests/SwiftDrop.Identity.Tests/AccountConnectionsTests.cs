using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Npgsql;

using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Api.Infrastructure;
using SwiftDrop.Identity.Tests.Persistence;

namespace SwiftDrop.Identity.Tests;

[Collection(IdentityPostgresCollection.Name)]
public sealed class AccountConnectionsTests(IdentityPostgresFixture database)
{
    private const string Password = "Fictional new account password 8!";
    private static readonly Lazy<Task<string>> Hash = new(() => new Argon2PasswordHasher().HashAsync(Password, default));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MethodsAreSelfOnlySafeAndNeverCached(bool password, bool google)
    {
        using var factory = new IdentityApiFactory(database);
        var user = await GoogleUser(factory);
        if (password) await Execute("UPDATE public.users SET password_hash=@hash WHERE id=@id", user.Id, await Hash.Value);
        if (!google) await Execute("DELETE FROM public.external_identities WHERE user_id=@id", user.Id);
        using var client = factory.Client();
        using var anonymous = await client.GetAsync("/api/v1/auth/methods");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", user.Token);
        using var response = await client.GetAsync("/api/v1/auth/methods?userId=" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = await Json(response);
        Assert.Equal(["googleLinked", "hasPassword"], body.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(password, body.RootElement.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(google, body.RootElement.GetProperty("googleLinked").GetBoolean());
    }

    [Fact]
    public async Task GoogleOwnerCanAddPasswordAndUseEitherMethodWithoutChangingIdentityOrRoles()
    {
        using var factory = new IdentityApiFactory(database);
        var user = await GoogleUser(factory);
        using var client = Browser(factory, user);
        var token = await Challenge(factory, client, user.Subject, user.Email);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/password", new { idToken = token, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = await Json(response);
        Assert.True(body.RootElement.GetProperty("hasPassword").GetBoolean());
        Assert.True(body.RootElement.GetProperty("googleLinked").GetBoolean());
        Assert.DoesNotContain(Password, body.RootElement.GetRawText());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-swiftdrop.google-nonce=;", StringComparison.Ordinal));

        using var login = factory.Client();
        using var local = await login.PostAsJsonAsync("/api/v1/auth/login", new { user.Email, password = Password });
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        using var localBody = await Json(local);
        Assert.Equal(user.Id, localBody.RootElement.GetProperty("user").GetProperty("id").GetGuid());
        Assert.Equal("Customer", localBody.RootElement.GetProperty("user").GetProperty("roles")[0].GetString());
        var googleToken = Token(factory, user.Subject, user.Email, null);
        using var google = await login.PostAsJsonAsync("/api/v1/auth/google", new { idToken = googleToken });
        Assert.Equal(HttpStatusCode.OK, google.StatusCode);
        using var googleBody = await Json(google);
        Assert.Equal(user.Id, googleBody.RootElement.GetProperty("user").GetProperty("id").GetGuid());
        Assert.Equal(1L, await Scalar<long>("SELECT COUNT(*) FROM public.audit_logs WHERE subject_user_id=@id AND action='PasswordAdded'", user.Id));
        var stored = await Scalar<string>("SELECT password_hash FROM public.users WHERE id=@id", user.Id);
        Assert.StartsWith("argon2id$", stored);
        Assert.True(await new Argon2PasswordHasher().VerifyAsync(Password, stored, default));
        var audit = await Scalar<string>("SELECT jsonb_agg(to_jsonb(a))::text FROM public.audit_logs a WHERE subject_user_id=@id", user.Id);
        Assert.DoesNotContain(Password, audit);
        Assert.DoesNotContain(stored, audit);
        Assert.DoesNotContain(token, audit);
    }

    [Theory]
    [InlineData("missing-nonce")]
    [InlineData("wrong-nonce")]
    [InlineData("expired-nonce")]
    [InlineData("invalid-token")]
    [InlineData("foreign-subject")]
    [InlineData("inactive")]
    public async Task InvalidReauthenticationCannotAddACredential(string fault)
    {
        using var factory = new IdentityApiFactory(database);
        var user = await GoogleUser(factory);
        using var client = Browser(factory, user);
        var token = await Challenge(factory, client, user.Subject, user.Email, fault);
        if (fault == "expired-nonce") factory.Clock.Now = factory.Clock.Now.AddMinutes(11);
        if (fault == "inactive") await Execute("UPDATE public.users SET is_active=FALSE WHERE id=@id", user.Id);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/password", new { idToken = token, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0L, await Scalar<long>("SELECT COUNT(*) FROM public.users WHERE id=@id AND password_hash IS NOT NULL", user.Id));
        Assert.Equal(0L, await Scalar<long>("SELECT COUNT(*) FROM public.audit_logs WHERE subject_user_id=@id AND action='PasswordAdded'", user.Id));
    }

    [Theory]
    [InlineData("csrf")]
    [InlineData("origin")]
    [InlineData("jwt")]
    [InlineData("short-password")]
    [InlineData("extra-field")]
    [InlineData("duplicate-field")]
    public async Task InvalidBrowserRequestsAreRejectedBeforeWriting(string fault)
    {
        using var factory = new IdentityApiFactory(database);
        var user = await GoogleUser(factory);
        using var client = Browser(factory, user);
        var token = await Challenge(factory, client, user.Subject, user.Email);
        if (fault == "csrf") client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        if (fault == "origin") { client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", "https://foreign.example.test"); }
        if (fault == "jwt") client.DefaultRequestHeaders.Authorization = null;
        var json = fault switch
        {
            "short-password" => JsonSerializer.Serialize(new { idToken = token, password = "short" }),
            "extra-field" => JsonSerializer.Serialize(new { idToken = token, password = Password, userId = Guid.NewGuid() }),
            "duplicate-field" => "{\"idToken\":\"" + token + "\",\"password\":\"short\",\"password\":\"" + Password + "\"}",
            _ => JsonSerializer.Serialize(new { idToken = token, password = Password })
        };
        using var response = await client.PostAsync("/api/v1/auth/password", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(fault is "csrf" or "origin" ? HttpStatusCode.Forbidden : fault == "jwt" ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await Scalar<long>("SELECT COUNT(*) FROM public.users WHERE id=@id AND password_hash IS NOT NULL", user.Id));
    }

    [Fact]
    public async Task ExistingPasswordCannotBeOverwritten()
    {
        using var factory = new IdentityApiFactory(database);
        var user = await GoogleUser(factory);
        var hash = await Hash.Value;
        await Execute("UPDATE public.users SET password_hash=@hash WHERE id=@id", user.Id, hash);
        using var client = Browser(factory, user);
        var token = await Challenge(factory, client, user.Subject, user.Email);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/password", new { idToken = token, password = "Different fictional password 9!" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(hash, await Scalar<string>("SELECT password_hash FROM public.users WHERE id=@id", user.Id));
    }

    [Fact]
    public async Task ConcurrentAddsCommitOnePasswordAndOneAuditRecord()
    {
        using var factory = new IdentityApiFactory(database);
        var user = await GoogleUser(factory);
        var store = new PostgresIdentityAuthenticationStore(database.DataSource);
        var identity = new VerifiedGoogleIdentity(user.Subject, user.Email, "Test", null);
        var hash = await Hash.Value;
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            store.AddPasswordAsync(user.Id, identity, hash, factory.Clock.Now, default)));
        Assert.Single(results, result => result.Account is not null);
        Assert.Single(results, result => result.Failure == IdentityFailure.PasswordAlreadySet);
        Assert.Equal(1L, await Scalar<long>("SELECT COUNT(*) FROM public.audit_logs WHERE subject_user_id=@id AND action='PasswordAdded'", user.Id));
    }

    [Fact]
    public async Task AuditFailureRollsBackPasswordAddition()
    {
        using var factory = new IdentityApiFactory(database);
        var user = await GoogleUser(factory);
        await Execute("""
            CREATE FUNCTION public.profile_password_audit_reject() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.action='PasswordAdded' THEN RAISE EXCEPTION 'Deliberate isolated credential audit rejection'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER profile_password_audit_reject BEFORE INSERT ON public.audit_logs
            FOR EACH ROW EXECUTE FUNCTION public.profile_password_audit_reject();
            """, user.Id);
        try
        {
            var store = new PostgresIdentityAuthenticationStore(database.DataSource);
            var hash = await Hash.Value;
            await Assert.ThrowsAsync<PostgresException>(() => store.AddPasswordAsync(user.Id,
                new(user.Subject, user.Email, "Test", null), hash, factory.Clock.Now, default));
            Assert.Equal(0L, await Scalar<long>("SELECT COUNT(*) FROM public.users WHERE id=@id AND password_hash IS NOT NULL", user.Id));
        }
        finally
        {
            await Execute("DROP TRIGGER profile_password_audit_reject ON public.audit_logs; DROP FUNCTION public.profile_password_audit_reject()", user.Id);
        }
    }

    private async Task<Account> GoogleUser(IdentityApiFactory factory)
    {
        var email = "connections-" + Guid.NewGuid().ToString("N") + "@example.test";
        var subject = Guid.NewGuid().ToString("N");
        using var client = factory.Client();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = Token(factory, subject, email, null) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await Json(response);
        return new(body.RootElement.GetProperty("user").GetProperty("id").GetGuid(), subject, email,
            body.RootElement.GetProperty("accessToken").GetString()!);
    }

    private static HttpClient Browser(IdentityApiFactory factory, Account user)
    {
        var client = factory.Client(cookies: true);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        client.DefaultRequestHeaders.Add("Origin", IdentityApiFactory.Origin);
        return client;
    }

    private static async Task<string> Challenge(IdentityApiFactory factory, HttpClient client, string subject, string email, string? fault = null)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await Json(response);
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", body.RootElement.GetProperty("csrfToken").GetString());
        return fault == "invalid-token" ? "invalid-test-token" : Token(factory,
            fault == "foreign-subject" ? Guid.NewGuid().ToString("N") : subject, email,
            fault == "missing-nonce" ? null : fault == "wrong-nonce" ? "wrong" : body.RootElement.GetProperty("googleNonce").GetString());
    }

    private static string Token(IdentityApiFactory factory, string subject, string email, string? nonce)
    {
        var token = Guid.NewGuid().ToString("N");
        factory.Google.Tokens[token] = new(subject, email, "Fictional Connection User", nonce);
        return token;
    }

    private async Task Execute(string sql, Guid id, string? hash = null)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        if (hash is not null) command.Parameters.AddWithValue("hash", hash);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> Scalar<T>(string sql, Guid id)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        return Assert.IsType<T>(await command.ExecuteScalarAsync());
    }

    private static async Task<JsonDocument> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    private sealed record Account(Guid Id, string Subject, string Email, string Token);
}
