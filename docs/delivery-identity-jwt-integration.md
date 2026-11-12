# Delivery Identity JWT integration

A successful login was followed by GET /api/v1/parcels, a Delivery 401, a
successful Identity refresh and another Delivery 401. The frontend cleared the
session. Delivery still registered an unconfigured handler instead of verifying
Identity's real access tokens.

## Implemented fix

Delivery registers JwtBearer with raw sub/roles claim mapping and verifies RS256
signatures using Identity's public /.well-known/jwks.json. Issuer and a single
exact audience must match; tokens require UUID sub, jti, an allowlisted roles
array and iat/nbf/exp with a maximum 900-second lifetime and 30-second clock skew.
Google ID tokens, service audiences, unsigned/foreign/expired tokens and caller
identity headers cannot grant Customer access. Existing resource ownership,
role, concurrency and idempotency policies remain unchanged.

Public keys use IdentityModel's asynchronous configuration manager with five
minute automatic refresh and throttled unknown-key refresh. Metadata requests
have a five-second bound, a 64 KiB limit and no redirects. Private/malformed/empty
key sets fail closed. No Identity database connection or private signing key is
shared with Delivery. No new public endpoint, token issuer or login mechanism
is introduced. Missing/unsafe configuration yields 401 while /health works.

## Configuration

Set Identity:Tokens:Issuer, Audience and JwksUrl to the values used by Identity.
Development appsettings use https://identity.swiftdrop.invalid,
swiftdrop-api-local and http://127.0.0.2:8080/.well-known/jwks.json. Docker must
use http://identity-service:8080/.well-known/jwks.json. AllowInsecureJwks=true
permits only loopback or that exact internal development hostname over HTTP;
all other metadata endpoints require HTTPS. These are public configuration
values, not new secrets. The infra companion PR supplies both host/Docker forms.

The frontend companion PR verifies global session validity through Identity
refresh and preserves a valid session after a repeated resource-service 401.
Only GET requests are retried automatically; mutations retain caller-controlled
idempotent recovery. A rejected Identity refresh still clears the session.

## Verification

Focused JWT tests cover valid Customer subject/role mapping, other-role 403s,
incorrect issuer/audience/signature/key, missing required claims, invalid role
shapes, expiration/lifetime and malformed/unavailable/oversized metadata. The
existing 31 cross-service cases now exercise this production verifier with real
Identity login/JWKS output and owned PostgreSQL fixtures, replacing their former
test-only JWT validator. No shared Neon migration or real user credential is
used by these tests. Live deployment still requires the configured endpoints,
network access and current schemas; no authentication bypass is provided.

The final Release solution build passed with zero warnings/errors. All 1,589
tests passed: Identity 369, Delivery 980, Merchant 167, Gateway 68 and Worker 5.
The companion frontend passed 239 tests plus the production browser login-loop
regression; the infra runner passed 12 isolated tests and Compose validation.

Implementation references: [JwtBearer configuration](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.authentication.jwtbearer.jwtbeareroptions?view=aspnetcore-10.0)
and [IdentityModel configuration management](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.protocols.configurationmanager-1?view=msal-web-dotnet-latest).
