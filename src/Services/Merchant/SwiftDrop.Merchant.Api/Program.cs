using SwiftDrop.Auditing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SwiftDrop.Merchant.Api.Application;
using SwiftDrop.Merchant.Api.Application.Merchants;
using SwiftDrop.Merchant.Api.Application.Orders;
using SwiftDrop.Merchant.Api.Application.Orders.Dashboard;
using SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication;
using SwiftDrop.Merchant.Api.Infrastructure.Orders;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Background;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Network;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Security;
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

        // Merchant orders (FR-M04 / FR-M07)
        builder.Services.AddScoped<SubmitMerchantOrderHandler>();
        builder.Services.AddScoped<MerchantOrderQueryHandler>();
        builder.Services.AddMerchantOrderProcessing(builder.Configuration, builder.Environment);
        builder.Services.AddScoped<MerchantDashboardHandler>();

        // Webhook infrastructure, ports & handlers
        builder.Services.AddScoped<ICurrentMerchantContext, ClaimsCurrentMerchantContext>();
        builder.Services.AddSingleton<IWebhookClock>(SystemWebhookClock.Instance);
        builder.Services.AddSingleton<IWebhookSecretProtector, AesGcmSecretProtector>();
        builder.Services.AddSingleton<IWebhookDestinationValidator, SsrfDestinationValidator>();
        builder.Services.AddSingleton<IWebhookSender, ResilientWebhookSender>();
        builder.Services.AddScoped<IWebhookOrderResolver, InboxJobWebhookOrderResolver>();

        builder.Services.AddScoped<CreateWebhookSubscriptionHandler>();
        builder.Services.AddScoped<ListWebhookSubscriptionsHandler>();
        builder.Services.AddScoped<DeleteWebhookSubscriptionHandler>();
        builder.Services.AddScoped<ListWebhookAttemptsHandler>();
        builder.Services.AddScoped<ReplayWebhookJobHandler>();
        builder.Services.AddScoped<TriggerTestWebhookHandler>();
        builder.Services.AddScoped<AcceptStatusChangedDispatchHandler>();
        builder.Services.AddScoped<ProcessDueWebhookJobsHandler>();

        builder.Services.AddHostedService<WebhookDispatchBackgroundService>();

        var identityJwks = new IdentityJwksKeyResolver(builder.Configuration);
        builder.Services.AddSingleton(identityJwks);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = identityJwks.CreateValidationParameters();
            })
            .AddMerchantApiKey();
        builder.Services.AddAuthorization();
        builder.Services.AddMerchantOrderAuthorization();

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
        app.UseActivityAudit("Merchant", "/api/v1/merchant/audit-logs");
        app.UseAuthorization();

        // Process liveness only; dependency readiness is a later milestone.
        app.MapHealthChecks("/health");
        app.MapMerchantEndpoints();
        app.MapWebhookEndpoints();
        app.MapOrderEndpoints();
        app.MapDashboardEndpoints();

        app.Run();
    }
}
