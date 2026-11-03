using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Merchant.Api.Application;
using SwiftDrop.Merchant.Api.Application.Merchants;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;
using SwiftDrop.Merchant.Api.Presentation;

namespace SwiftDrop.Merchant.Api;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHealthChecks();
        builder.Services.AddMerchantPersistence(builder.Configuration);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<MerchantHandler>();
        builder.Services.AddAuthentication(UnconfiguredIdentityHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, UnconfiguredIdentityHandler>(
                UnconfiguredIdentityHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        // Explicit single-run migration task; normal host startup never applies DDL.
        if (args.Contains("--migrate", StringComparer.Ordinal))
        {
            using var migrationHost = builder.Build();
            using var scope = migrationHost.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MerchantDbContext>();
            database.Database.Migrate();
            return;
        }

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var supplied = context.Request.Headers["X-Correlation-ID"];
            var correlation = supplied.Count == 1 && supplied[0] is { Length: > 0 and <= 128 } value
                && value.All(character => character is >= '!' and <= '~')
                    ? value : Guid.NewGuid().ToString("N");
            context.Items["MerchantCorrelationId"] = correlation;
            context.Response.Headers["X-Correlation-ID"] = correlation;
            await next(context);
        });
        app.UseExceptionHandler(handler => handler.Run(context => MerchantProblems.Unexpected(context).ExecuteAsync(context)));
        app.UseStatusCodePages(async status =>
        {
            var context = status.HttpContext;
            if (context.Response.StatusCode is 401 or 403)
            {
                var failure = context.Response.StatusCode == 401
                    ? MerchantFailure.Unauthenticated
                    : MerchantFailure.Forbidden;
                await MerchantProblems.Failure(context, failure).ExecuteAsync(context);
            }
        });
        app.UseAuthentication();
        app.UseAuthorization();

        // Process liveness only; dependency readiness is a later milestone.
        app.MapHealthChecks("/health");
        app.MapMerchantEndpoints();

        app.Run();
    }
}
