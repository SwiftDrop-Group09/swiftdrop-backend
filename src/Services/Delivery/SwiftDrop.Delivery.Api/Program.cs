using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Infrastructure.Authentication;
using SwiftDrop.Delivery.Api.Infrastructure.Bookings;
using SwiftDrop.Delivery.Api.Infrastructure.DeliveryReads;
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
        builder.Services.AddDeliveryPersistence(builder.Configuration);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentCustomer, ClaimsCurrentCustomer>();
        builder.Services.AddScoped<ICurrentDeliveryUser, ClaimsCurrentDeliveryUser>();
        builder.Services.AddScoped<OperationsClock>();
        builder.Services.AddScoped<DeliveryReadHandler>();
        builder.Services.AddScoped<IDeliveryReadStore>(services =>
            new PostgresDeliveryReadStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<QuoteEstimator>();
        builder.Services.AddScoped<CustomerBookingHandler>();
        builder.Services.AddSingleton<ITrackingNumberGenerator, CryptographicTrackingNumberGenerator>();
        // Storage is resolved only after use-case authorization and validation.
        // Invalid requests still have their proper response when DB settings are absent.
        builder.Services.AddScoped<IQuoteStore>(services =>
            new PostgresQuoteStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddScoped<IBookingStore>(services =>
            new PostgresBookingStore(() => services.GetRequiredService<DeliveryDbContext>()));
        builder.Services.AddAuthentication(UnconfiguredIdentityHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, UnconfiguredIdentityHandler>(
                UnconfiguredIdentityHandler.SchemeName, _ => { });
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
        app.UseExceptionHandler(handler => handler.Run(context => QuoteProblems.Unexpected(context).ExecuteAsync(context)));
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<DeliveryReadEndpointMetadata>() is not null)
                context.Response.Headers.CacheControl = "private, no-store";
            await next(context);
        });
        app.UseStatusCodePages(async status =>
        {
            var context = status.HttpContext;
            if (context.Response.StatusCode is 401 or 403)
            {
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
        app.UseAuthorization();

        // Process liveness only; dependency readiness is a later milestone.
        app.MapHealthChecks("/health");
        app.MapCustomerQuotes();
        app.MapCustomerBookings();
        app.MapDeliveryReads();

        app.Run();
    }
}
