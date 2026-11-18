using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication.ApiKeys;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication.OrderAccess;

namespace SwiftDrop.Merchant.Api.Infrastructure.Authentication;

public static class MerchantOrderAuthenticationExtensions
{
    /// <summary>
    /// Adds the <c>X-API-Key</c> scheme beside the default scheme. It is not made
    /// the default, so existing endpoints stay JWT-only and ignore API keys.
    /// </summary>
    public static AuthenticationBuilder AddMerchantApiKey(this AuthenticationBuilder builder) =>
        builder.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationDefaults.SchemeName, configureOptions: null);

    /// <summary>Registers the order caller resolver and the OrdersRead / OrdersWrite policies.</summary>
    public static IServiceCollection AddMerchantOrderAuthorization(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentOrderCaller, ClaimsCurrentOrderCaller>();
        services.AddScoped<IAuthorizationHandler, OrderAccessHandler>();
        services.AddSingleton<IConfigureOptions<AuthorizationOptions>, MerchantOrderPolicySetup>();
        return services;
    }
}
