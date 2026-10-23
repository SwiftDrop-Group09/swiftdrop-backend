using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SwiftDrop.Delivery.Api.Infrastructure.Authentication;

// Fail closed until the Identity integration supplies verified authentication.
// Never parse bearer tokens or X-User-* headers as an authenticated identity.
// Tests replace this scheme only inside the test project's host configuration.
public sealed class UnconfiguredIdentityHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DeliveryIdentityUnavailable";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return base.HandleChallengeAsync(properties);
    }
}
