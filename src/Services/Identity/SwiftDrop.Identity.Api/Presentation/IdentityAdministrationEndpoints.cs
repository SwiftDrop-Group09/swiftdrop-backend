using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http.Features;

using Npgsql;

using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Api.Infrastructure;

namespace SwiftDrop.Identity.Api.Presentation;

public static class IdentityAdministrationEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8
    };

    public static void MapIdentityAdministration(this WebApplication app)
    {
        Map(app, "GET", "/api/v1/users/me", async context => Result(await Store(context)
            .GetProfileAsync(Actor(context), context.RequestAborted)));
        Map(app, "PUT", "/api/v1/users/me", UpdateProfileAsync);
        Map(app, "GET", "/api/v1/users", ListUsersAsync, admin: true);
        Map(app, "POST", "/api/v1/users", CreateUserAsync, admin: true);
        Map(app, "PUT", "/api/v1/users/{id:guid}", UpdateUserAsync, admin: true);
        Map(app, "DELETE", "/api/v1/users/{id:guid}", DeactivateAsync, admin: true);
        Map(app, "PUT", "/api/v1/users/{id:guid}/roles", RolesAsync, admin: true);
        Map(app, "GET", "/api/v1/audit-logs", ListAuditAsync, admin: true);
    }

    private static void Map(WebApplication app, string method, string route,
        Func<HttpContext, Task<IResult>> handler, bool admin = false)
    {
        var endpoint = app.MapMethods(route, [method], async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (method != "GET" && BrowserAuthentication.IsBrowser(context)
                    && !await context.RequestServices.GetRequiredService<BrowserAuthentication>().ValidateAsync(context))
                {
                    return IdentityEndpoints.Problem(403, "csrf_rejected", "Browser request verification failed.");
                }

                if (context.Request.ContentLength > 16384) return Invalid();
                var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (size is { IsReadOnly: false }) size.MaxRequestBodySize = 16384;
                return await handler(context);
            }
            catch (Exception exception) when (exception is JsonException or BadHttpRequestException or NotSupportedException
                || exception is InvalidOperationException { InnerException: ArgumentException })
            {
                return Invalid();
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                return IdentityEndpoints.Problem(503, "identity_unavailable", "Identity account management is unavailable.");
            }
        }).RequireAuthorization().RequireRateLimiting("identity-management");
        if (admin) endpoint.RequireAuthorization(policy => policy.RequireRole("Admin"));
    }

    private static async Task<IResult> UpdateProfileAsync(HttpContext context)
    {
        var input = await ReadAsync<ProfileInput>(context);
        if (input is null || !ValidName(input.FullName) || input.PhoneNumber?.Length > 32) return Invalid();
        return Result(await Store(context).UpdateProfileAsync(Actor(context),
            new ProfileUpdate(input.FullName!.Trim(), Clean(input.PhoneNumber), Clean(input.Address)),
            Now(context), Correlation(context), context.RequestAborted));
    }

    private static async Task<IResult> ListUsersAsync(HttpContext context)
    {
        if (!Page(context, out var page, out var size)) return Invalid();
        return Result(await Store(context).ListUsersAsync(Actor(context), page, size, context.RequestAborted));
    }

    private static async Task<IResult> ListAuditAsync(HttpContext context)
    {
        if (!Page(context, out var page, out var size)) return Invalid();
        return Result(await Store(context).ListAuditAsync(Actor(context), page, size, context.RequestAborted));
    }

    private static async Task<IResult> UpdateUserAsync(HttpContext context)
    {
        var input = await ReadAsync<AdminInput>(context);
        if (input is null || !ValidEmail(input.Email) || !ValidName(input.FullName) || input.IsActive is null) return Invalid();
        var email = input.Email!.Trim();
        return Result(await Store(context).UpdateUserAsync(Actor(context), Target(context), email, email.ToUpperInvariant(),
            input.FullName!.Trim(), input.IsActive.Value, Now(context), Correlation(context), context.RequestAborted));
    }

    private static async Task<IResult> CreateUserAsync(HttpContext context)
    {
        var input = await ReadAsync<CreateInput>(context);
        if (input is null || !ValidEmail(input.Email) || !ValidName(input.FullName) || input.IsActive is null
            || input.Password is not { Length: >= 12 and <= 128 }) return Invalid();
        var email = input.Email!.Trim();
        var hash = await context.RequestServices.GetRequiredService<Argon2PasswordHasher>()
            .HashAsync(input.Password, context.RequestAborted);
        var result = await Store(context).CreateUserAsync(Actor(context), email, email.ToUpperInvariant(),
            input.FullName!.Trim(), input.IsActive.Value, hash, Now(context), Correlation(context), context.RequestAborted);
        return result.Failure is { } failure ? Failure(failure) : Results.Json(result.Value, statusCode: 201);
    }

    private static async Task<IResult> RolesAsync(HttpContext context)
    {
        var input = await ReadAsync<RolesInput>(context);
        if (input?.Roles is null || input.Roles.Length > 5 || input.Roles.Any(string.IsNullOrWhiteSpace)
            || input.Roles.Distinct(StringComparer.Ordinal).Count() != input.Roles.Length) return Invalid();
        return Result(await Store(context).SetRolesAsync(Actor(context), Target(context), input.Roles,
            Now(context), Correlation(context), context.RequestAborted));
    }

    private static async Task<IResult> DeactivateAsync(HttpContext context)
    {
        if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0) return Invalid();
        var result = await Store(context).DeactivateUserAsync(Actor(context), Target(context),
            Now(context), Correlation(context), context.RequestAborted);
        return result.Failure is null ? Results.NoContent() : Failure(result.Failure.Value);
    }

    private static async Task<T?> ReadAsync<T>(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new JsonException();
        return await context.Request.ReadFromJsonAsync<T>(InputJson, context.RequestAborted);
    }

    private static bool Page(HttpContext context, out int page, out int size)
    {
        page = 1;
        size = 20;
        if (context.Request.Query.Keys.Any(key => key is not ("page" or "pageSize"))) return false;
        return ReadPage("page", ref page) && ReadPage("pageSize", ref size) && page >= 1 && size is >= 1 and <= 100;

        bool ReadPage(string key, ref int value)
        {
            var supplied = context.Request.Query[key];
            return supplied.Count == 0 || supplied.Count == 1
                && int.TryParse(supplied[0], NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
    }

    private static IResult Result<T>(IdentityAdministrationResult<T> result) => result.Failure is { } failure
        ? Failure(failure) : Results.Ok(result.Value);
    private static IResult Failure(IdentityAdministrationFailure failure) => failure switch
    {
        IdentityAdministrationFailure.Forbidden => IdentityEndpoints.Problem(403, "forbidden", "Account access is forbidden."),
        IdentityAdministrationFailure.NotFound => IdentityEndpoints.Problem(404, "not_found", "The account was not found."),
        IdentityAdministrationFailure.LastAdmin => IdentityEndpoints.Problem(409, "last_active_admin", "At least one active Admin must remain."),
        IdentityAdministrationFailure.InvalidRoles => Invalid(),
        _ => IdentityEndpoints.Problem(409, "account_conflict", "The account change conflicts with existing state.")
    };
    private static IResult Invalid() => IdentityEndpoints.Problem(400, "validation", "The account request is invalid.");
    private static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 120;
    private static bool ValidEmail(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 254
        && new EmailAddressAttribute().IsValid(value.Trim());
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static Guid Actor(HttpContext context) => Guid.Parse(context.User.FindFirst("sub")!.Value);
    private static Guid Target(HttpContext context) => Guid.Parse((string)context.Request.RouteValues["id"]!);
    private static DateTimeOffset Now(HttpContext context) => context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
    private static string Correlation(HttpContext context) => context.TraceIdentifier;
    private static IIdentityAdministrationStore Store(HttpContext context) => context.RequestServices.GetRequiredService<IIdentityAdministrationStore>();
    private sealed record ProfileInput(string? FullName, [property: JsonRequired] string? PhoneNumber, string? Address);
    private sealed record AdminInput(string? Email, string? FullName, bool? IsActive);
    private sealed record CreateInput(string? Email, string? FullName, bool? IsActive, string? Password);
    private sealed record RolesInput(string[]? Roles);
}
