using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SwiftDrop.Delivery.Tests.Authentication;

// This fixture is compiled only into the test assembly. Normal Development and
// Production hosts never register it; request headers never select a user.
internal static class DeliveryTestAuthentication
{
    public const string SchemeName = "DeliveryTests";
    public static readonly Guid CustomerId = new("11111111-1111-4111-8111-111111111111");
    public static readonly Guid OtherCustomerId = new("22222222-2222-4222-8222-222222222222");
    public static readonly Guid NonCustomerId = new("33333333-3333-4333-8333-333333333333");

    public static ClaimsPrincipal Customer(Guid? ownerId = null) =>
        Principal((ownerId ?? CustomerId).ToString("D"), "Customer");

    public static ClaimsPrincipal Principal(string? subject, params string[] roles)
    {
        var claims = roles.Select(role => new Claim(ClaimTypes.Role, role)).ToList();
        if (subject is not null)
        {
            claims.Add(new Claim("sub", subject));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
    }

    public static void AddFixedPrincipal(IServiceCollection services, ClaimsPrincipal? principal)
    {
        services.AddSingleton(new FixedPrincipal(principal));
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = SchemeName;
            options.DefaultChallengeScheme = SchemeName;
            options.DefaultForbidScheme = SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, FixedPrincipalHandler>(SchemeName, _ => { });
    }

    private sealed record FixedPrincipal(ClaimsPrincipal? Value);

    private sealed class FixedPrincipalHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, FixedPrincipal principal)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            principal.Value?.Identities.Any(identity => identity.IsAuthenticated) == true
                ? AuthenticateResult.Success(new AuthenticationTicket(principal.Value.Clone(), SchemeName))
                : AuthenticateResult.NoResult());
    }
}
