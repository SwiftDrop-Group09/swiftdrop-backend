using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using JsonWebToken = Microsoft.IdentityModel.JsonWebTokens.JsonWebToken;
using SwiftDrop.Merchant.Api.Application;
using SwiftDrop.Merchant.Api.Application.Merchants;
using SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Background;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Network;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Security;
using SwiftDrop.Merchant.Api.Presentation;

namespace SwiftDrop.Merchant.Api;

public partial class Program
{
    private const string WorkerServiceScheme = "MerchantWorkerService";
    private const string WorkerServicePolicy = "MerchantWorkerService";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHealthChecks();
        builder.Services.AddMerchantPersistence(builder.Configuration);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<MerchantHandler>();

        // Webhook infrastructure, ports & handlers
        builder.Services.AddScoped<ICurrentMerchantContext, ClaimsCurrentMerchantContext>();
        builder.Services.AddSingleton<IWebhookClock>(SystemWebhookClock.Instance);
        builder.Services.AddSingleton<IWebhookSecretProtector, AesGcmSecretProtector>();
        builder.Services.AddSingleton<IWebhookDestinationValidator, SsrfDestinationValidator>();
        builder.Services.AddSingleton<IWebhookSender, ResilientWebhookSender>();
        builder.Services.AddScoped<IWebhookOrderResolver, StubWebhookOrderResolver>();

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
            .AddJwtBearer(WorkerServiceScheme, options =>
            {
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = identityJwks.CreateValidationParameters("merchant-internal");
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (!ValidWorkerServiceClaims(context.SecurityToken))
                        {
                            context.Fail("Invalid worker service token.");
                        }

                        return Task.CompletedTask;
                    }
                };
            });
        builder.Services.AddAuthorization(options => options.AddPolicy(WorkerServicePolicy, policy =>
        {
            policy.AddAuthenticationSchemes(WorkerServiceScheme);
            policy.RequireAuthenticatedUser();
        }));

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
        app.MapWebhookEndpoints();

        app.Run();
    }

    private static bool ValidWorkerServiceClaims(Microsoft.IdentityModel.Tokens.SecurityToken token)
    {
        var encoded = token switch
        {
            JsonWebToken jwt => jwt.EncodedPayload,
            JwtSecurityToken jwt => jwt.RawPayload,
            _ => null
        };
        if (encoded is null) return false;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.Decode(encoded));
            var root = document.RootElement;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return root.TryGetProperty("sub", out var subject) && subject.ValueKind == JsonValueKind.String &&
                subject.GetString() == "notification-worker" &&
                root.TryGetProperty("jti", out var jti) && jti.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(jti.GetString()) &&
                root.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String &&
                scope.GetString()!.Split(' ') is ["webhooks:dispatch"] &&
                Number(root, "iat", out var issued) && Number(root, "nbf", out var notBefore) &&
                Number(root, "exp", out var expires) && issued >= 0 && issued <= now + 30 &&
                notBefore == issued && expires > issued && expires - issued <= 300;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Number(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value);
    }
}
