using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.IdentityModel.Tokens;

using Npgsql;

using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Tests.Persistence;

namespace SwiftDrop.Identity.Tests;

[Collection(IdentityPostgresCollection.Name)]
public sealed class SessionApiTests(IdentityPostgresFixture database)
{
    private const string Password = "Correct Horse Battery 7!";

    [Fact]
    public async Task MobileRegistrationAndLoginIssueVerifiableCustomerJwtAndHashOnlyRefresh()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = NewEmail();
        var user = await RegisterAsync(client, " " + email.ToUpperInvariant() + " ");
        var tokens = await LoginAsync(client, email);

        Assert.Equal(user.Id, tokens.User.Id);
        Assert.Equal(["Customer"], tokens.User.Roles);
        Assert.Equal("Bearer", tokens.TokenType);
        Assert.Equal(900, tokens.ExpiresInSeconds);
        Assert.Equal(32, Base64UrlEncoder.DecodeBytes(tokens.RefreshToken).Length);
        Assert.Equal(43, tokens.RefreshToken.Length);

        using var response = await client.GetAsync("/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var jwks = await response.Content.ReadAsStringAsync();
        var principal = Handler().ValidateToken(tokens.AccessToken, Validation(jwks), out var validated);
        var jwt = Assert.IsType<JwtSecurityToken>(validated);
        Assert.Equal("RS256", jwt.Header.Alg);
        Assert.Equal("test-key", jwt.Header.Kid);
        Assert.Equal(user.Id.ToString("D"), jwt.Subject);
        Assert.True(principal.IsInRole("Customer"));
        Assert.False(principal.IsInRole("Admin"));
        Assert.False(principal.IsInRole("Courier"));
        Assert.Equal(TimeSpan.FromMinutes(15), jwt.ValidTo - jwt.ValidFrom);
        Assert.NotEmpty(jwt.Id);
        using (var publicKeys = JsonDocument.Parse(jwks))
        {
            var key = Assert.Single(publicKeys.RootElement.GetProperty("keys").EnumerateArray());
            Assert.Equal(["alg", "e", "kid", "kty", "n", "use"], key.EnumerateObject().Select(item => item.Name).Order().ToArray());
        }

        var storedHash = await ScalarAsync<string>("SELECT password_hash FROM public.users WHERE id=@id", ("id", user.Id));
        Assert.StartsWith("argon2id$v=19$m=65536,t=3,p=1$", storedHash);
        Assert.DoesNotContain(Password, storedHash);
        Assert.Equal(Hash(tokens.RefreshToken), await ScalarAsync<string>(
            "SELECT token_hash FROM public.refresh_sessions WHERE user_id=@id", ("id", user.Id)));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_sessions WHERE token_hash=@raw", ("raw", tokens.RefreshToken)));
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_families WHERE user_id=@id AND expires_at-created_at=INTERVAL '30 days'", ("id", user.Id)));
        Assert.DoesNotContain("password", JsonSerializer.Serialize(tokens.User), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password_hash", jwks);
        Assert.DoesNotContain("PRIVATE KEY", jwks);
    }

    [Fact]
    public async Task RegistrationRejectsNormalizedDuplicateWithoutReturningAccountDetails()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = NewEmail();
        await RegisterAsync(client, email);
        using var duplicate = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email = " " + email.ToUpperInvariant() + " ", password = Password, fullName = "Duplicate Customer" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var body = await duplicate.Content.ReadAsStringAsync();
        Assert.Contains("registration_conflict", body);
        Assert.DoesNotContain(email, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, body);
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.users WHERE normalized_email=@email", ("email", email.ToUpperInvariant())));
    }

    [Fact]
    public async Task RegistrationCannotChoosePrivilegedRoles()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = NewEmail(),
            password = Password,
            fullName = "Customer",
            roles = new[] { "Admin", "Courier", "Merchant" },
            isActive = true
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<RegisteredProfile>();
        Assert.NotNull(profile);
        Assert.Equal(["Customer"], profile.Roles);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM public.user_roles WHERE user_id=@id", ("id", profile.Id)));
        Assert.Equal("Customer", await ScalarAsync<string>("SELECT role_name FROM public.user_roles WHERE user_id=@id", ("id", profile.Id)));
    }

    [Theory]
    [InlineData("incorrect-password")]
    [InlineData("unknown-account")]
    [InlineData("disabled-account")]
    public async Task InvalidLocalCredentialsHaveSameGenericResponseAndCreateNoSession(string condition)
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = NewEmail();
        var user = await RegisterAsync(client, email);
        if (condition == "disabled-account")
        {
            await ExecuteAsync("UPDATE public.users SET is_active=FALSE WHERE id=@id", ("id", user.Id));
        }

        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = condition == "unknown-account" ? NewEmail() : email,
            password = condition == "incorrect-password" ? "Incorrect Horse Battery 7!" : Password
        });
        await AssertInvalidCredentialsAsync(response);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM public.refresh_families WHERE user_id=@id", ("id", user.Id)));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM public.refresh_sessions WHERE user_id=@id", ("id", user.Id)));
        Assert.DoesNotContain(email, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FifthFailedLoginLocksAccountUntilOneMinuteHasElapsed()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = NewEmail();
        var user = await RegisterAsync(client, email);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Incorrect Password!" });
            await AssertInvalidCredentialsAsync(failed);
        }

        using (var locked = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password }))
        {
            await AssertInvalidCredentialsAsync(locked);
        }

        Assert.Equal(1, await ScalarAsync<int>("SELECT lockout_count FROM public.users WHERE id=@id", ("id", user.Id)));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM public.refresh_sessions WHERE user_id=@id", ("id", user.Id)));
        factory.Clock.Now = factory.Clock.Now.AddMinutes(1);
        Assert.Equal(user.Id, (await LoginAsync(client, email)).User.Id);
        Assert.Equal(0, await ScalarAsync<int>("SELECT lockout_count FROM public.users WHERE id=@id", ("id", user.Id)));
    }

    [Fact]
    public async Task MobileRefreshRotatesBodyTokenWithoutCookiesAndPreservesAbsoluteExpiry()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = NewEmail();
        await RegisterAsync(client, email);
        var initial = await LoginAsync(client, email);
        var originalExpiry = await TimestampAsync(
            "SELECT expires_at FROM public.refresh_families WHERE user_id=@id", ("id", initial.User.Id));
        factory.Clock.Now = factory.Clock.Now.AddDays(29);
        using var response = await RefreshAsync(client, initial.RefreshToken);
        var replacement = await ReadTokensAsync(response);
        Assert.NotEqual(initial.RefreshToken, replacement.RefreshToken);
        Assert.NotEqual(initial.AccessToken, replacement.AccessToken);
        Assert.Equal(initial.User.Id, replacement.User.Id);
        Assert.Equal(originalExpiry, await TimestampAsync(
            "SELECT expires_at FROM public.refresh_sessions WHERE token_hash=@hash", ("hash", Hash(replacement.RefreshToken))));
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_sessions WHERE token_hash=@hash AND consumed_at IS NOT NULL AND replaced_by_session_id IS NOT NULL",
            ("hash", Hash(initial.RefreshToken))));
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_families WHERE user_id=@id", ("id", initial.User.Id)));
    }

    [Fact]
    public async Task ReplayingConsumedMobileTokenRevokesItsReplacement()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = NewEmail();
        await RegisterAsync(client, email);
        var initial = await LoginAsync(client, email);
        using var first = await RefreshAsync(client, initial.RefreshToken);
        var replacement = await ReadTokensAsync(first);
        using var replay = await RefreshAsync(client, initial.RefreshToken);
        await AssertInvalidCredentialsAsync(replay);
        using var revoked = await RefreshAsync(client, replacement.RefreshToken);
        await AssertInvalidCredentialsAsync(revoked);
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_sessions WHERE user_id=@id AND revoked_at IS NULL", ("id", initial.User.Id)));
    }

    [Fact]
    public async Task ConcurrentMobileRefreshReturnsOneSuccessAndRevokesReturnedReplacementOnReuse()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = NewEmail();
        await RegisterAsync(client, email);
        var initial = await LoginAsync(client, email);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Enumerable.Range(0, 2).Select(async _ =>
        {
            await start.Task;
            return await RefreshAsync(client, initial.RefreshToken);
        }).ToArray();
        start.SetResult();
        var responses = await Task.WhenAll(pending);
        try
        {
            var success = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            await AssertInvalidCredentialsAsync(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Unauthorized));
            var replacement = await ReadTokensAsync(success);
            using var revoked = await RefreshAsync(client, replacement.RefreshToken);
            await AssertInvalidCredentialsAsync(revoked);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task OwnLogoutRevokesFamilyWhileExistingAccessJwtRemainsValidUntilItsExpiry()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = NewEmail();
        await RegisterAsync(client, email);
        var first = await LoginAsync(client, email);
        var otherLogin = await LoginAsync(client, email);
        using (var logout = await LogoutAsync(client, first.AccessToken, first.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            Assert.False(logout.Headers.Contains("Set-Cookie"));
        }

        var jwks = await client.GetStringAsync("/.well-known/jwks.json");
        Handler().ValidateToken(first.AccessToken, Validation(jwks), out var validated);
        var jwt = Assert.IsType<JwtSecurityToken>(validated);
        Assert.Equal(TimeSpan.FromSeconds(900), jwt.ValidTo - jwt.ValidFrom);
        using (var revoked = await RefreshAsync(client, first.RefreshToken))
        {
            await AssertInvalidCredentialsAsync(revoked);
        }

        // This independently issued family can still be logged out using the
        // same unexpired access JWT after the first family has been revoked.
        using var secondLogout = await LogoutAsync(client, first.AccessToken, otherLogin.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, secondLogout.StatusCode);
        using var otherRevoked = await RefreshAsync(client, otherLogin.RefreshToken);
        await AssertInvalidCredentialsAsync(otherRevoked);
    }

    [Fact]
    public async Task ForeignUsersJwtCannotLogoutAnotherUsersRefreshFamily()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var ownerEmail = NewEmail();
        var foreignEmail = NewEmail();
        await RegisterAsync(client, ownerEmail);
        await RegisterAsync(client, foreignEmail);
        var owner = await LoginAsync(client, ownerEmail);
        var foreign = await LoginAsync(client, foreignEmail);
        using var denied = await LogoutAsync(client, foreign.AccessToken, owner.RefreshToken);
        await AssertInvalidCredentialsAsync(denied);
        using var stillValid = await RefreshAsync(client, owner.RefreshToken);
        Assert.Equal(owner.User.Id, (await ReadTokensAsync(stillValid)).User.Id);
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM public.refresh_families WHERE user_id=@id AND revoked_at IS NOT NULL", ("id", owner.User.Id)));
    }

    [Fact]
    public async Task AnonymousLogoutIsRejectedWithoutRevokingPresentedFamily()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = NewEmail();
        await RegisterAsync(client, email);
        var initial = await LoginAsync(client, email);
        using var denied = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = initial.RefreshToken });
        await AssertInvalidCredentialsAsync(denied);
        using var stillValid = await RefreshAsync(client, initial.RefreshToken);
        Assert.Equal(initial.User.Id, (await ReadTokensAsync(stillValid)).User.Id);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("disabled-account")]
    [InlineData("revoked-token")]
    [InlineData("revoked-family")]
    public async Task RefreshRejectsExpiredRevokedAndDisabledSessions(string condition)
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        var email = NewEmail();
        await RegisterAsync(client, email);
        var initial = await LoginAsync(client, email);
        if (condition == "expired")
        {
            factory.Clock.Now = factory.Clock.Now.AddDays(30);
        }
        else if (condition == "disabled-account")
        {
            await ExecuteAsync("UPDATE public.users SET is_active=FALSE WHERE id=@id", ("id", initial.User.Id));
        }
        else if (condition == "revoked-token")
        {
            await ExecuteAsync("UPDATE public.refresh_sessions SET revoked_at=created_at WHERE user_id=@id", ("id", initial.User.Id));
        }
        else
        {
            await ExecuteAsync("UPDATE public.refresh_families SET revoked_at=created_at WHERE user_id=@id", ("id", initial.User.Id));
        }

        using var response = await RefreshAsync(client, initial.RefreshToken);
        await AssertInvalidCredentialsAsync(response);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM public.refresh_sessions WHERE user_id=@id", ("id", initial.User.Id)));
    }

    [Theory]
    [InlineData("/api/v1/auth/login", "{\"email\":\"customer@example.test\",\"password\":\"password\",\"roles\":[\"Admin\"]}")]
    [InlineData("/api/v1/auth/login", "{\"email\":\"first@example.test\",\"email\":\"second@example.test\",\"password\":\"password\"}")]
    [InlineData("/api/v1/auth/login", "{\"email\":7,\"password\":\"password\"}")]
    [InlineData("/api/v1/auth/login", "null")]
    [InlineData("/api/v1/auth/login", "{")]
    [InlineData("/api/v1/auth/refresh", "{\"refreshToken\":\"token\",\"roles\":[\"Admin\"]}")]
    [InlineData("/api/v1/auth/refresh", "{\"refreshToken\":7}")]
    [InlineData("/api/v1/auth/refresh", "{}")]
    public async Task MalformedOrUnknownAuthenticationFieldsReturnSafeValidationProblem(string route, string json)
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(route, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"validation\"", body);
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("password_hash", body);
    }

    [Fact]
    public async Task UnsupportedAuthenticationCharsetReturnsSafeValidationProblem()
    {
        using var factory = new IdentityApiFactory(database);
        using var client = factory.Client();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        content.Headers.ContentType!.CharSet = "unsupported-swiftdrop-charset";
        using var response = await client.PostAsync("/api/v1/auth/login", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"code\":\"validation\"", await response.Content.ReadAsStringAsync());
    }

    private static async Task<RegisteredProfile> RegisterAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = Password, fullName = "Customer Identity" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, body);
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", body, StringComparison.OrdinalIgnoreCase);
        return Assert.IsType<RegisteredProfile>(await response.Content.ReadFromJsonAsync<RegisteredProfile>());
    }

    private static async Task<MobileTokens> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        return await ReadTokensAsync(response);
    }

    private static async Task<MobileTokens> ReadTokensAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, body);
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password_hash", body, StringComparison.OrdinalIgnoreCase);
        var tokens = await response.Content.ReadFromJsonAsync<MobileTokens>();
        Assert.NotNull(tokens);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        return tokens;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });

    private static async Task<HttpResponseMessage> LogoutAsync(HttpClient client, string accessToken, string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout")
        {
            Content = JsonContent.Create(new { refreshToken })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    private static async Task AssertInvalidCredentialsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_credentials", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("The supplied credentials are invalid.", body.RootElement.GetProperty("title").GetString());
        Assert.False(body.RootElement.TryGetProperty("accessToken", out _));
        Assert.False(body.RootElement.TryGetProperty("refreshToken", out _));
        Assert.False(body.RootElement.TryGetProperty("user", out _));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static TokenValidationParameters Validation(string jwks) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = IdentityApiFactory.Origin,
        ValidateAudience = true,
        ValidAudience = "swiftdrop-api",
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(),
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "sub",
        RoleClaimType = "roles"
    };

    private static JwtSecurityTokenHandler Handler() => new() { MapInboundClaims = false };
    private static string NewEmail() => Guid.NewGuid().ToString("N") + "@example.test";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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

    private sealed record MobileTokens(string AccessToken, string TokenType, int ExpiresInSeconds,
        RegisteredProfile User, string RefreshToken);
}
