using SwiftDrop.Auditing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Contracts.Events.V1;
using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.Admin.Couriers;
using SwiftDrop.Delivery.Api.Application.Admin.Pricing;
using SwiftDrop.Delivery.Api.Application.Admin.Reports;
using SwiftDrop.Delivery.Api.Application.Admin.Zones;
using SwiftDrop.Delivery.Api.Application.Assistant;
using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.CourierDuty;
using SwiftDrop.Delivery.Api.Application.CourierStatus;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Application.Dispatch;
using SwiftDrop.Delivery.Api.Application.Notifications;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Infrastructure.Admin;
using SwiftDrop.Delivery.Api.Infrastructure.Assistant;
using SwiftDrop.Delivery.Api.Infrastructure.Authentication;
using SwiftDrop.Delivery.Api.Infrastructure.Bookings;
using SwiftDrop.Delivery.Api.Infrastructure.CourierStatus;
using SwiftDrop.Delivery.Api.Infrastructure.CourierProof;
using SwiftDrop.Delivery.Api.Infrastructure.DeliveryReads;
using SwiftDrop.Delivery.Api.Infrastructure.Dispatch;
using SwiftDrop.Delivery.Api.Infrastructure.Notifications;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Api.Infrastructure.Quotes;
using SwiftDrop.Delivery.Api.Presentation;

namespace SwiftDrop.Delivery.Api;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHealthChecks();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
                await context.HttpContext.Response.WriteAsJsonAsync(new { code = "rate_limit_exceeded" }, ct);
            options.AddConcurrencyLimiter(DeliveryReadEndpoints.PublicTrackingStreamsPolicy, limiter =>
            {
                limiter.PermitLimit = builder.Configuration.GetValue("Tracking:PublicStreamLimit", 64);
                limiter.QueueLimit = 0;
            });
        });
        builder.Services.AddGrpc(options => options.MaxReceiveMessageSize = 16 * 1024);
        builder.Services.AddScoped<MerchantDeliveryGrpcService>();
        builder.Services.AddScoped<Func<DeliveryDbContext>>(services => () => services.GetRequiredService<DeliveryDbContext>());
        builder.Services.AddDeliveryPersistence(builder.Configuration);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentCustomer, ClaimsCurrentCustomer>();
        builder.Services.AddScoped<ICurrentDeliveryUser, ClaimsCurrentDeliveryUser>();
        builder.Services.AddScoped<OperationsClock>();
        builder.Services.AddScoped<DeliveryReadHandler>();
        builder.Services.AddScoped<CustomerTrackingHandler>();
        builder.Services.AddScoped<AssignmentHandler>();
        builder.Services.AddScoped<IAssignmentStore>(services =>
            new PostgresAssignmentStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<CourierWorkloadHandler>();
        builder.Services.AddScoped<IWorkloadReadStore>(services =>
            new PostgresWorkloadReadStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddSingleton<IReceiptPdfRenderer, SkiaReceiptPdfRenderer>();
        builder.Services.AddScoped<CourierHistoryHandler>();
        builder.Services.AddScoped<CourierHistoryDetailHandler>();
        builder.Services.AddScoped<ICourierHistoryDetailStore>(services =>
            new PostgresDeliveryReadStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<CourierSummaryHandler>();
        builder.Services.AddScoped<ICourierSummaryStore>(services =>
            new PostgresDeliveryReadStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<ICourierHistoryStore>(services =>
            new PostgresDeliveryReadStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<CourierStatusHandler>();
        builder.Services.AddScoped<CourierProofHandler>();
        builder.Services.AddSingleton<IProofImageValidator, SkiaProofImageValidator>();
        builder.Services.AddScoped<IPrivateProofStorage>(services =>
            new PostgresProofStorage(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<ICourierProofStore>(services =>
            new PostgresCourierStatusStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<IProofCleanupStore>(services =>
            new PostgresProofCleanupStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<ProofCleanup>();
        builder.Services.AddHostedService<ProofCleanupWorker>();
        builder.Services.AddScoped<ICourierStatusStore>(services =>
            new PostgresCourierStatusStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<IDeliveryReadStore>(services =>
            new PostgresDeliveryReadStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<INotificationContextStore, PostgresNotificationContextStore>();
        builder.Services.AddScoped<NotificationContextHandler>();
        builder.Services.AddScoped<ICustomerDeliveryReadStore>(services =>
            new PostgresDeliveryReadStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<IPrivateProofReader>(services =>
            new PostgresProofStorage(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<QuoteEstimator>();
        builder.Services.AddScoped<CustomerBookingHandler>();
        builder.Services.AddSingleton<ITrackingNumberGenerator, CryptographicTrackingNumberGenerator>();
        // Storage is resolved only after use-case authorization and validation.
        // Invalid requests still have their proper response when DB settings are absent.
        builder.Services.AddScoped<IQuoteStore>(services =>
            new PostgresQuoteStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<IBookingZoneCatalog>(services =>
            new PostgresBookingZoneCatalog(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<IBookingStore>(services =>
            new PostgresBookingStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<IZoneAdministrationStore>(services =>
            new PostgresZoneAdministrationStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<ZoneAdministrationHandler>();
        builder.Services.AddScoped<IPricingAdministrationStore>(services =>
            new PostgresPricingAdministrationStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<PricingAdministrationHandler>();
        builder.Services.AddScoped<IDeliveryReportStore>(services =>
            new PostgresDeliveryReportStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<DeliveryReportHandler>();
        builder.Services.AddScoped<CourierDutyHandler>();
        builder.Services.AddScoped<AdminCourierHandler>();
        builder.Services.AddScoped<AssistantToolExecutor>();
        builder.Services.AddHttpClient<MerchantAssistantOrderClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(3);
            client.MaxResponseContentBufferSize = 64 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        });
        builder.Services.AddScoped<McpServerHandler>();
        builder.Services.AddScoped<AssistantOrchestrator>();
        builder.Services.AddHttpClient("MerchantInternal");

        builder.Services.AddDeliveryIdentityAuthentication();
        builder.Services.AddAuthorization();

        // Explicit single-run migration task; normal host startup never applies DDL.
        if (args.Contains("--migrate", StringComparer.Ordinal))
        {
            using var migrationHost = builder.Build();
            using var scope = migrationHost.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
            database.Database.Migrate();
            return;
        }

        var app = builder.Build();
        app.MapGrpcService<MerchantDeliveryGrpcService>().AllowAnonymous();

        app.Use(async (context, next) =>
        {
            var supplied = context.Request.Headers["X-Correlation-ID"];
            var correlation = supplied.Count == 1 && supplied[0] is { Length: > 0 and <= 128 } value
                && value.All(character => character is >= '!' and <= '~')
                    ? value : Guid.NewGuid().ToString("N");
            context.Items["DeliveryCorrelationId"] = correlation;
            context.Response.Headers["X-Correlation-ID"] = correlation;
            await next(context);
        });
        app.UseExceptionHandler(handler => handler.Run(context =>
        {
            var isAdmin = context.Request.Path.StartsWithSegments("/api/v1/zones")
                || context.Request.Path.StartsWithSegments("/api/v1/pricing-tiers")
                || context.Request.Path.StartsWithSegments("/api/v1/reports/deliveries");
            var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            if (isAdmin && (error is DbUpdateConcurrencyException || error is Npgsql.PostgresException { SqlState: "23505" or "40001" or "40P01" }))
                return AdminProblems.Conflict(context).ExecuteAsync(context);
            if (isAdmin && error is System.Data.Common.DbException or DbUpdateException or InvalidOperationException or TimeoutException)
                return AdminProblems.Unavailable(context).ExecuteAsync(context);
            return QuoteProblems.Unexpected(context).ExecuteAsync(context);
        }));
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<AssignmentEndpointMetadata>() is not null
                || context.GetEndpoint()?.Metadata.GetMetadata<DeliveryReadEndpointMetadata>() is not null
                || context.GetEndpoint()?.Metadata.GetMetadata<CourierStatusEndpointMetadata>() is not null
                || context.GetEndpoint()?.Metadata.GetMetadata<CourierProofEndpointMetadata>() is not null
                || context.GetEndpoint()?.Metadata.GetMetadata<AdminZoneEndpointMetadata>() is not null
                || context.GetEndpoint()?.Metadata.GetMetadata<AdminPricingEndpointMetadata>() is not null
                || context.GetEndpoint()?.Metadata.GetMetadata<AdminReportEndpointMetadata>() is not null
                || context.GetEndpoint()?.Metadata.GetMetadata<AssistantEndpointMetadata>() is not null)
                context.Response.Headers.CacheControl = "private, no-store";
            await next(context);
        });
        app.UseStatusCodePages(async status =>
        {
            var context = status.HttpContext;
            if (context.Response.StatusCode is 401 or 403)
            {
                if (context.GetEndpoint()?.Metadata.GetMetadata<AssignmentEndpointMetadata>() is not null)
                {
                    await AssignmentEndpoints.Problem(context, context.Response.StatusCode == 401
                        ? AssignmentFailure.Unauthenticated : AssignmentFailure.Forbidden).ExecuteAsync(context);
                    return;
                }
                if (context.GetEndpoint()?.Metadata.GetMetadata<CourierStatusEndpointMetadata>() is not null
                    || context.GetEndpoint()?.Metadata.GetMetadata<CourierProofEndpointMetadata>() is not null)
                {
                    await CourierStatusProblems.Failure(context, context.Response.StatusCode == 401
                        ? CourierStatusFailure.Unauthenticated : CourierStatusFailure.Forbidden).ExecuteAsync(context);
                    return;
                }
                if (context.GetEndpoint()?.Metadata.GetMetadata<DeliveryReadEndpointMetadata>() is not null)
                {
                    await DeliveryReadProblems.Failure(context, context.Response.StatusCode == 401
                        ? DeliveryReadFailure.Unauthenticated : DeliveryReadFailure.Forbidden).ExecuteAsync(context);
                    return;
                }
                var failure = context.Response.StatusCode == 401 ? QuoteFailure.Unauthenticated : QuoteFailure.Forbidden;
                await QuoteProblems.Failure(context, failure).ExecuteAsync(context);
            }
        });
        app.UseAuthentication();
        app.UseActivityAudit("Delivery", "/api/v1/delivery/audit-logs");
        app.UseAuthorization();
        app.UseRateLimiter();

        // Process liveness only; dependency readiness is a later milestone.
        app.MapHealthChecks("/health");
        app.MapCustomerQuotes();
        app.MapCustomerBookings();
        app.MapDeliveryReads();
        app.MapDispatchReads();
        app.MapAssignments();
        app.MapCourierDuty();
        app.MapNotificationContext();
        app.MapCourierStatus();
        app.MapCourierProof();
        app.MapAdminCouriers();
        app.MapAdminZones();
        app.MapAdminPricing();
        app.MapAdminReports();
        app.MapAssistantEndpoints();

        app.Run();
    }
}
