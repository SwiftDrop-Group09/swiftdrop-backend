using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SwiftDrop.Merchant.Api.Infrastructure.Authentication.ApiKeys;

namespace SwiftDrop.Merchant.Api.Infrastructure.Authentication.OrderAccess;

/// <summary>
/// Builds the order policies once authentication is configured. The human
/// (owner) scheme is whatever the application's default scheme is — JWT in
/// production, the test scheme in integration tests — so the policies never
/// hard-code a scheme name that hosts may replace.
/// </summary>
internal sealed class MerchantOrderPolicySetup(IOptions<AuthenticationOptions> authentication)
    : IConfigureOptions<AuthorizationOptions>
{
    public void Configure(AuthorizationOptions options)
    {
        var ownerScheme = authentication.Value.DefaultAuthenticateScheme ?? authentication.Value.DefaultScheme;

        options.AddPolicy(MerchantAuthorizationPolicies.OrdersWrite, policy => policy
            .AddAuthenticationSchemes(ApiKeyAuthenticationDefaults.SchemeName)
            .RequireAuthenticatedUser()
            .AddRequirements(new OrderAccessRequirement(OrderAccess.Write)));

        options.AddPolicy(MerchantAuthorizationPolicies.OrdersRead, policy =>
        {
            policy.AddAuthenticationSchemes(ApiKeyAuthenticationDefaults.SchemeName);
            if (ownerScheme is not null)
            {
                policy.AddAuthenticationSchemes(ownerScheme);
            }

            policy.RequireAuthenticatedUser()
                .AddRequirements(new OrderAccessRequirement(OrderAccess.Read));
        });
    }
}
