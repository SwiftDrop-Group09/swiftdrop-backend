using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Api.Infrastructure;
using SwiftDrop.Identity.Api.Infrastructure.Authentication;
using SwiftDrop.Identity.Api.Infrastructure.Google;
using SwiftDrop.Identity.Api.Presentation;

namespace SwiftDrop.Identity.Api;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var applyMigrations = args.Contains("--migrate", StringComparer.Ordinal);
        var generateDevelopmentKey = args.Contains("--generate-development-key", StringComparer.Ordinal);
        var provisionDevelopmentAdmin = args.Contains("--provision-development-admin", StringComparer.Ordinal);
        if (new[] { applyMigrations, generateDevelopmentKey, provisionDevelopmentAdmin }.Count(value => value) > 1)
            throw new InvalidOperationException("Select only one Identity provisioning operation.");
        var builderArgs = args.Where(argument => argument is not ("--migrate" or "--generate-development-key" or "--provision-development-admin")).ToArray();
        var builder = WebApplication.CreateBuilder(builderArgs);

        if (provisionDevelopmentAdmin)
        {
            await DevelopmentAdminProvisioner.ProvisionAsync(builder.Configuration, builder.Environment);
            return;
        }
        if (generateDevelopmentKey)
        {
            DevelopmentSigningKey.Create(builder.Configuration, builder.Environment);
            return;
        }

        if (applyMigrations)
        {
            var connectionString = builder.Configuration.GetConnectionString("MigrationDatabase");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:MigrationDatabase must be configured to apply Identity migrations.");
            }

            await IdentitySchemaMigrator.ApplyAsync(connectionString);
            return;
        }

        builder.Services.AddHealthChecks();
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier);
        builder.Services.AddSingleton(sp =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Database");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:Database must be configured before using Identity data.");
            }

            if (new NpgsqlConnectionStringBuilder(connectionString).Database != "swiftdrop_identity")
            {
                throw new InvalidOperationException("Identity may connect only to its swiftdrop_identity database.");
            }

            return NpgsqlDataSource.Create(connectionString);
        });
        builder.Services.AddScoped<IUserRegistrationStore, PostgresUserRegistrationStore>();
        builder.Services.AddSingleton<Argon2PasswordHasher>();
        builder.Services.AddSingleton<IIdentityPasswordVerifier>(services => services.GetRequiredService<Argon2PasswordHasher>());
        builder.Services.AddSingleton<IGoogleIdentityVerifier, GoogleIdentityVerifier>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IIdentityAuthenticationStore, PostgresIdentityAuthenticationStore>();
        builder.Services.AddScoped<IIdentityAdministrationStore, PostgresIdentityAdministrationStore>();
        builder.Services.AddScoped(services => new IdentityAuthenticationService(
            () => services.GetRequiredService<IIdentityAuthenticationStore>(),
            services.GetRequiredService<IIdentityPasswordVerifier>(), services.GetRequiredService<IIdentityAccessTokens>(),
            services.GetRequiredService<IGoogleIdentityVerifier>(), services.GetRequiredService<TimeProvider>()));
        builder.Services.AddIdentityTokenAuthentication(builder.Configuration);
        builder.Services.AddDataProtection();
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-Token";
            options.Cookie.Name = BrowserAuthentication.CsrfCookie;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });
        builder.Services.AddScoped<BrowserAuthentication>();
        builder.Services.AddCors(options => options.AddPolicy("identity-browser", policy =>
        {
            var origins = builder.Configuration.GetSection("Identity:Browser:AllowedOrigins").Get<string[]>() ?? [];
            policy.WithOrigins(origins.Where(BrowserAuthentication.ValidOrigin).ToArray())
                .WithMethods("GET", "POST", "PUT", "DELETE")
                .WithHeaders("Content-Type", "Authorization", "X-CSRF-Token", "X-Correlation-ID")
                .WithExposedHeaders("X-Correlation-ID")
                .AllowCredentials();
        }));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("identity-management", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst("sub")?.Value ?? GetClientIpAddress(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.AddPolicy("identity-auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.AddPolicy("registration", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    GetClientIpAddress(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
        });

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var supplied = context.Request.Headers["X-Correlation-ID"];
            var value = supplied.Count == 1 ? supplied[0] : null;
            // Accept only opaque UUIDs. Never persist arbitrary caller text or
            // a credential accidentally placed in the correlation header.
            value = Guid.TryParseExact(value, "D", out var correlation) && correlation != Guid.Empty
                ? correlation.ToString("D") : Guid.NewGuid().ToString("D");
            context.TraceIdentifier = value;
            context.Response.Headers["X-Correlation-ID"] = value;
            await next(context);
        });
        app.MapHealthChecks("/health");
        app.UseIdentityProxyForwarding(builder.Configuration);
        app.UseCors("identity-browser");
        app.UseStatusCodePages(async status =>
        {
            if (status.HttpContext.Response.StatusCode is 401 or 403)
            {
                await IdentityEndpoints.Problem(status.HttpContext.Response.StatusCode,
                    status.HttpContext.Response.StatusCode == 403 ? "forbidden" : "invalid_credentials",
                    status.HttpContext.Response.StatusCode == 403 ? "Account access is forbidden."
                        : "The supplied credentials are invalid.").ExecuteAsync(status.HttpContext);
            }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapIdentityAuthentication();
        app.MapIdentityAdministration();
        app.MapGet("/api/v1/auth/csrf", (HttpContext context, BrowserAuthentication browser) => browser.Bootstrap(context))
            .RequireRateLimiting("identity-auth");
        app.MapGet("/ready", async (
            IServiceProvider services,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Database")))
            {
                return Results.Problem(
                    type: "https://example.invalid/problems/database-not-configured",
                    title: "Identity database is not configured.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!await IsReadyAsync(services, cancellationToken))
            {
                return Results.Problem(
                    type: "https://example.invalid/problems/database-not-ready",
                    title: "Identity database migration is pending.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new { status = "ready" });
        });
        app.MapPost("/api/v1/auth/register", async (
            RegisterRequest input,
            IServiceProvider services,
            IConfiguration configuration,
            Argon2PasswordHasher passwordHasher,
            CancellationToken cancellationToken) =>
        {
            var errors = Validate(input);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(
                    errors,
                    statusCode: StatusCodes.Status400BadRequest,
                    type: "https://example.invalid/problems/validation",
                    title: "The request is invalid.");
            }

            if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Database")))
            {
                return Results.Problem(
                    type: "https://example.invalid/problems/database-not-configured",
                    title: "Customer registration is unavailable because Identity database access is not configured.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!await IsReadyAsync(services, cancellationToken))
            {
                return Results.Problem(
                    type: "https://example.invalid/problems/database-not-ready",
                    title: "Customer registration is unavailable until the Identity database migration is applied.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var email = input.Email.Trim();
            var store = services.GetRequiredService<IUserRegistrationStore>();
            var profile = await store.RegisterAsync(
                email,
                email.ToUpperInvariant(),
                input.FullName.Trim(),
                await passwordHasher.HashAsync(input.Password, cancellationToken),
                cancellationToken);
            if (profile is null)
            {
                return Results.Problem(
                    type: "https://example.invalid/problems/registration-conflict",
                    title: "Registration conflict.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?> { ["code"] = "registration_conflict" });
            }

            return Results.Json(profile, statusCode: StatusCodes.Status201Created);
        }).RequireRateLimiting("registration");

        app.Run();
    }

    private static Dictionary<string, string[]> Validate(RegisterRequest input)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(input.Email)
            || input.Email.Length > 254
            || !new EmailAddressAttribute().IsValid(input.Email.Trim()))
        {
            errors["email"] = ["Enter a valid email address of at most 254 characters."];
        }

        if (input.Password is null || input.Password.Length is < 12 or > 128)
        {
            errors["password"] = ["Password must be between 12 and 128 characters."];
        }

        if (string.IsNullOrWhiteSpace(input.FullName) || input.FullName.Trim().Length > 120)
        {
            errors["fullName"] = ["Full name is required and must be at most 120 characters."];
        }

        return errors;
    }

    private static string GetClientIpAddress(HttpContext context)
    {
        // Forwarded headers are processed only for explicitly trusted proxies.
        // Never let an arbitrary caller choose its limiter partition with XFF.
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static async Task<bool> IsReadyAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            return await services.GetRequiredService<IUserRegistrationStore>().IsReadyAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private sealed record RegisterRequest(string Email, string Password, string FullName);
}
