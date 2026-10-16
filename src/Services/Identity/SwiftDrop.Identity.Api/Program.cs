using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using SwiftDrop.Identity.Api.Application;
using SwiftDrop.Identity.Api.Infrastructure;

namespace SwiftDrop.Identity.Api;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var applyMigrations = args.Contains("--migrate", StringComparer.Ordinal);
        var builderArgs = args.Where(argument => argument != "--migrate").ToArray();
        var builder = WebApplication.CreateBuilder(builderArgs);

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
        builder.Services.AddSingleton(sp =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Database");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:Database must be configured before using Identity data.");
            }

            return NpgsqlDataSource.Create(connectionString);
        });
        builder.Services.AddScoped<IUserRegistrationStore, PostgresUserRegistrationStore>();
        builder.Services.AddSingleton<Argon2PasswordHasher>();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
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

        app.MapHealthChecks("/health");
        app.UseRateLimiter();
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

            var store = services.GetRequiredService<IUserRegistrationStore>();
            if (!await store.IsReadyAsync(cancellationToken))
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

            var store = services.GetRequiredService<IUserRegistrationStore>();
            if (!await store.IsReadyAsync(cancellationToken))
            {
                return Results.Problem(
                    type: "https://example.invalid/problems/database-not-ready",
                    title: "Customer registration is unavailable until the Identity database migration is applied.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var email = input.Email.Trim();
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
        var forwardedAddresses = context.Request.Headers["X-Forwarded-For"]
            .ToString()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (forwardedAddresses.Length > 0
            && IPAddress.TryParse(forwardedAddresses[^1], out var forwardedAddress))
        {
            return forwardedAddress.ToString();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private sealed record RegisterRequest(string Email, string Password, string FullName);
}
