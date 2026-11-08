using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http.Features;

using Npgsql;

using SwiftDrop.Identity.Api.Application;

namespace SwiftDrop.Identity.Api.Presentation;

public static class IdentityEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8
    };

    public static void MapIdentityAuthentication(this WebApplication app)
    {
        app.MapGet("/.well-known/jwks.json", (HttpContext context, IIdentityAccessTokens tokens) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "public,max-age=3600";
                return Results.Ok(tokens.PublicKeys());
            }
            catch (InvalidOperationException)
            {
                context.Response.Headers.CacheControl = "no-store";
                return Unavailable();
            }
        });

        Map(app, "/api/v1/auth/login", LoginAsync);
        Map(app, "/api/v1/auth/refresh", RefreshAsync);
        Map(app, "/api/v1/auth/logout", LogoutAsync, authenticated: true);
        Map(app, "/api/v1/auth/google", GoogleAsync);
        Map(app, "/api/v1/auth/google/link", LinkAsync, authenticated: true);
    }

    private static void Map(WebApplication app, string path, Func<HttpContext, Task<IResult>> handler,
        bool authenticated = false)
    {
        var endpoint = app.MapPost(path, async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (BrowserAuthentication.IsBrowser(context)
                    && !await context.RequestServices.GetRequiredService<BrowserAuthentication>().ValidateAsync(context))
                {
                    return Problem(403, "csrf_rejected", "Browser request verification failed.");
                }

                if (context.Request.ContentLength > 16384)
                {
                    return Invalid();
                }

                var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (size is { IsReadOnly: false })
                {
                    size.MaxRequestBodySize = 16384;
                }

                return await handler(context);
            }
            catch (Exception exception) when (exception is JsonException or BadHttpRequestException or NotSupportedException
                || exception is InvalidOperationException { InnerException: ArgumentException })
            {
                return Invalid();
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                return Unavailable();
            }
        }).RequireRateLimiting("identity-auth");
        if (authenticated)
        {
            endpoint.RequireAuthorization();
        }
    }

    private static async Task<IResult> LoginAsync(HttpContext context)
    {
        var input = await ReadAsync<LoginInput>(context);
        if (input is null || string.IsNullOrWhiteSpace(input.Email) || input.Email.Length > 254
            || !new EmailAddressAttribute().IsValid(input.Email.Trim()) || !ValidPassword(input.Password))
        {
            return Invalid();
        }

        var result = await Service(context).LoginAsync(input.Email, input.Password!, context.RequestAborted);
        return Tokens(context, result);
    }

    private static async Task<IResult> RefreshAsync(HttpContext context)
    {
        var input = await ReadAsync<RefreshInput>(context, BrowserAuthentication.IsBrowser(context));
        var token = RefreshToken(context, input);
        return token is null ? Invalid() : Tokens(context,
            await Service(context).RefreshAsync(token, context.RequestAborted));
    }

    private static async Task<IResult> LogoutAsync(HttpContext context)
    {
        if (!Guid.TryParse(context.User.FindFirst("sub")?.Value, out var userId))
        {
            return Failure(IdentityFailure.InvalidCredentials);
        }

        var input = await ReadAsync<RefreshInput>(context, BrowserAuthentication.IsBrowser(context));
        var token = RefreshToken(context, input);
        if (token is null)
        {
            return Invalid();
        }

        if (!await Service(context).LogoutAsync(userId, token, context.RequestAborted))
        {
            return Failure(IdentityFailure.InvalidCredentials);
        }

        if (BrowserAuthentication.IsBrowser(context))
        {
            BrowserAuthentication.ClearRefresh(context);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> GoogleAsync(HttpContext context)
    {
        var input = await ReadAsync<GoogleInput>(context);
        if (!ValidGoogleToken(input?.IdToken))
        {
            return Invalid();
        }

        var browser = BrowserAuthentication.IsBrowser(context);
        var nonce = browser ? context.RequestServices.GetRequiredService<BrowserAuthentication>().ExpectedGoogleNonce(context) : null;
        var result = await Service(context).GoogleAsync(input!.IdToken!, nonce, browser, context.RequestAborted);
        if (browser && result.User is not null)
        {
            BrowserAuthentication.ClearGoogleNonce(context);
        }

        return Tokens(context, result);
    }

    private static async Task<IResult> LinkAsync(HttpContext context)
    {
        var input = await ReadAsync<LinkInput>(context);
        if (!Guid.TryParse(context.User.FindFirst("sub")?.Value, out var userId)
            || !ValidGoogleToken(input?.IdToken) || !ValidPassword(input?.CurrentPassword))
        {
            return Invalid();
        }

        var browser = BrowserAuthentication.IsBrowser(context);
        var nonce = browser ? context.RequestServices.GetRequiredService<BrowserAuthentication>().ExpectedGoogleNonce(context) : null;
        var result = await Service(context).LinkGoogleAsync(userId, input!.IdToken!, input.CurrentPassword!,
            nonce, browser, context.RequestAborted);
        if (result.User is null)
        {
            return Failure(result.Failure ?? IdentityFailure.InvalidCredentials);
        }

        if (browser)
        {
            BrowserAuthentication.ClearGoogleNonce(context);
        }
        return Results.Ok(result.User);
    }

    private static IResult Tokens(HttpContext context, IdentityAuthenticationResult result)
    {
        if (result.User is null)
        {
            return Failure(result.Failure ?? IdentityFailure.InvalidCredentials);
        }

        var browser = BrowserAuthentication.IsBrowser(context);
        if (browser)
        {
            context.RequestServices.GetRequiredService<BrowserAuthentication>()
                .SetRefresh(context, result.RefreshToken!, result.RefreshExpiresAt!.Value);
        }

        return Results.Ok(new TokenPair(result.AccessToken!, "Bearer", 900, result.User,
            browser ? null : result.RefreshToken));
    }

    private static string? RefreshToken(HttpContext context, RefreshInput? input) =>
        BrowserAuthentication.IsBrowser(context)
            ? input?.RefreshToken is null
                ? context.RequestServices.GetRequiredService<BrowserAuthentication>().ReadRefresh(context) : null
            : input?.RefreshToken;

    private static async Task<T?> ReadAsync<T>(HttpContext context, bool allowEmpty = false)
    {
        if (allowEmpty && context.Request.ContentLength is null or 0 && !context.Request.HasJsonContentType())
        {
            return default;
        }

        if (!context.Request.HasJsonContentType())
        {
            throw new JsonException();
        }

        return await context.Request.ReadFromJsonAsync<T>(InputJson, context.RequestAborted);
    }

    private static IdentityAuthenticationService Service(HttpContext context) =>
        context.RequestServices.GetRequiredService<IdentityAuthenticationService>();
    private static bool ValidPassword(string? value) => value is { Length: > 0 and <= 128 };
    private static bool ValidGoogleToken(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 10000;
    private static IResult Invalid() => Problem(400, "validation", "The authentication request is invalid.");
    private static IResult Unavailable() => Problem(503, "identity_unavailable", "Identity authentication is unavailable.");
    public static IResult Problem(int status, string code, string title) => Results.Problem(statusCode: status,
        type: "https://example.invalid/problems/" + code, title: title,
        extensions: new Dictionary<string, object?> { ["code"] = code });
    private static IResult Failure(IdentityFailure failure) => failure switch
    {
        IdentityFailure.AccountLinkRequired => Problem(409, "account_link_required", "Sign in locally and explicitly link the Google account."),
        IdentityFailure.SubjectConflict => Problem(409, "google_identity_conflict", "The Google identity cannot be linked."),
        IdentityFailure.EmailMismatch => Problem(403, "account_link_rejected", "The Google identity cannot be linked."),
        _ => Problem(401, "invalid_credentials", "The supplied credentials are invalid.")
    };

    private sealed record LoginInput(string? Email, string? Password);
    private sealed record RefreshInput(string? RefreshToken);
    private sealed record GoogleInput(string? IdToken);
    private sealed record LinkInput(string? IdToken, string? CurrentPassword);
    private sealed record TokenPair(string AccessToken, string TokenType, int ExpiresInSeconds, RegisteredProfile User,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RefreshToken);
}
