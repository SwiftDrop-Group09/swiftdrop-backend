# Part A: Identity sessions and Google login/linking

## Scope and baseline

Reinspected on 25–26 October 2026. The initial branch was
`feat/delivery-quotes-verification` at `5780377`, with a clean tree. Its prior
463 passing tests concerned the Delivery milestone, not completed Identity
Phases 1–2. Identity contained only registration, the Argon2id hash writer,
Npgsql registration persistence, migration 001 and 12 scaffold/registration tests.
Login and JWKS were explicitly expected to be absent.

After fetching backend refs, main/dev were both `32604d4`; all other available
feature branches concerned Delivery. No missing Identity foundation was found.
The user then authorized implementing that foundation before Phases 3–4.
`feat/identity-sessions-google` starts from `5780377`, preserving the two earlier
local Delivery verification commits without changing their files/history.

Docs `d97fff4`, infra `075a8b4`, frontend `c062b11` and mobile `e9778c1` remain
clean on main. Their remotes were fetched and contained no newer main changes.
No AGENTS.md was found. Relevant current authentication/authorization, REST,
data/ER, Google/refresh diagrams, use cases, testing, deployment and decision
documents were read alongside source, migrations, manifests, CI and tests.
Architecture/security choices remain **Proposed**; implementation does not
manufacture team approval.

The assessment identified missing password verification, login, signing/JWKS,
bearer validation, session rotation and Google adapters. The implementation plan
was to reuse existing registration and SQL migration conventions; add focused
application ports/handlers, transactional session/provider persistence and
cryptographic adapters; then transport/browser protections and real database,
HTTP and security tests. No Identity EF migration strategy or shared user store
was introduced alongside the existing Npgsql/embedded-SQL design.

## Implemented behavior

| Route | Behavior |
| --- | --- |
| POST /api/v1/auth/register | Existing validated Customer-only registration/profile response retained; no privileged caller-selected role |
| POST /api/v1/auth/login | Normalized lookup, existing Argon2id verification, generic invalid credentials, active/lockout checks, application access/refresh session |
| GET /.well-known/jwks.json | Public RSA keys only; current plus explicitly retained previous verification keys; 503 when signing configuration is unavailable |
| POST /api/v1/auth/refresh | Opaque token verification, atomic rotation, fresh account/roles, original family expiry; consumed-token reuse revokes the family |
| POST /api/v1/auth/logout | Verified human bearer plus caller-owned refresh family; 204 and browser-cookie deletion; foreign family is not revoked |
| POST /api/v1/auth/google | Verified Google ID token to application session; provider/subject lookup, active account, atomic new Customer-only registration or account_link_required collision |
| POST /api/v1/auth/google/link | Verified human bearer, fresh currentPassword and valid matching-email Google identity; atomic subject uniqueness and credential snapshot check; safe profile response |
| GET /api/v1/auth/csrf | Supporting browser bootstrap: protected host-only antiforgery/nonce cookies and JSON csrfToken/googleNonce; no-store |

Register still accepts email/password/fullName. Login uses email/password;
Google uses only idToken; linking uses idToken/currentPassword. Native refresh
and logout use refreshToken. New authentication bodies reject extra/duplicate
fields, malformed types and unsupported encodings. Profile DTOs expose no local
hash, provider token, session hash or key material. Body size/depth limits,
per-client throttling and database account lockout bound authentication work.

Access JWTs use RS256 with kid, configured HTTPS issuer/exact audience, UUID
sub, roles array, iat/nbf/exp and jti; lifetime is 900 seconds and validation skew
is 30 seconds. Signature, algorithm, issuer, exact audience, lifetime, known key
and required human claims are verified. No forwarded role/user header or API key
authenticates a human. JWKS is not a complete OIDC authorization server; no
discovery/code/callback or public workload-token minting endpoint is added.

Refresh secrets contain 256 random bits, represented as 43 base64url characters.
Only SHA-256 hashes reach PostgreSQL. A session family expires after 30 days;
rotation does not extend its absolute expiry. User/family/session row locks
serialize rotation and reuse revocation. A concurrent duplicate refresh may
produce one successful response followed by family revocation on the reuse;
the replacement then fails safely. Expired/revoked/disabled accounts cannot
refresh. Existing access JWTs remain cryptographically valid until their expiry
(subject to the explicit clock skew); logout is not immediate downstream JWT
revocation. Old hashes remain available for reuse detection; no cleanup job is
claimed.

Local failures lock the account after five attempts: 1, 2, 4, 8, then at most 15
minutes on subsequent lockouts. Successful local authentication resets counters.
Unknown accounts perform a bounded dummy derivation and receive the same
ordinary credentials response. Verified password snapshots are compared again
under the user lock for session creation/linking, preventing password-change
races from authorizing stale reauthentication.

Google uses [Google.Apis.Auth 1.77.0](https://www.nuget.org/packages/Google.Apis.Auth),
with signature/issuer/audience/time verification before any decoded field is
used. Strict claim types, verified email, nonempty stable subject, configured
Web/Android audiences and applicable authorized-party/nonce checks are enforced.
Multiple audiences require an allowed azp. Provider failure/cancellation or
unverifiable tokens cause no identity/session mutation. No Google access token
is accepted as an application bearer.

An existing linked subject authenticates its existing user even if the provider
email later changes; local profiles are not silently merged/overwritten. A new
subject colliding with normalized local email returns 409 account_link_required.
Explicit linking requires local password ownership and matching verified email;
different-email linking is rejected as deferred. Google-only accounts have no
local password. The client/platform cannot grant Courier/Merchant/Admin roles;
authorized existing roles come from Identity associations. Google linking and
new account audit writes share their state-changing transaction, using only IDs,
fixed action codes and timestamps.

## Browser and client contract

The supporting bootstrap route/header/cookie names were unspecified in the
baseline. This implementation makes them explicit rather than changing existing
request-body schemas:

1. From a configured same-site HTTPS origin, GET /api/v1/auth/csrf with credentials.
   Keep csrfToken in memory, and pass googleNonce to the browser Google SDK's
   nonce option when requesting an ID token.
2. Send X-CSRF-Token and credentials on browser login, Google/link, refresh and
   logout; mutation Origin must exactly match Identity:Browser:AllowedOrigins.
3. Browser refresh/logout omit refreshToken from JSON (an empty object/body is
   accepted). Origin, browser fetch metadata or cookies select browser handling;
   mixing a body refresh token with browser transport is rejected.
4. The refresh cookie is Secure, HttpOnly, SameSite=Strict, host-only,
   Path=/api/v1/auth, with original family expiry. Responses omit refreshToken.
   Browser access JWTs remain client-memory data; no localStorage work is added.
5. A protected refresh-cookie envelope is bound to the __Host-swiftdrop.csrf
   cookie. This prevents a refresh cookie from another browser changing the
   caller's session while using its valid CSRF proof. There is no raw-cookie
   fallback. Losing the CSRF context requires login again.

Antiforgery is tied to the protected host cookie independently of the expiring
bearer principal, so refresh works without an access token. Browser Google nonce
cookies are protected, expire after ten minutes and are cleared on success.
Unbound/mismatched/expired nonces are rejected. Native clients use body refresh
tokens without Origin/cookies and platform secure storage; normal native SDK
tokens without a nonce are supported. A native SDK supplying a nonce needs an
agreed server-issued challenge-binding integration; arbitrary caller-provided
expected nonces are never trusted. Actual SDK/device integration belongs to the
client phase and has not been exercised here.

Only explicitly configured proxy IPs may supply forwarded client IP, host and
scheme. No implicit loopback or arbitrary X-Forwarded-For trust exists. The
bounded trust chain restores public HTTPS/host for same-origin bootstrap and
isolates rate limits by client IP. Without trusted-proxy configuration headers
are ignored; behind a Gateway the limiter will otherwise see the Gateway peer.
No forwarded user/role claims are trusted under either configuration.

## Database and configuration

Migration **002_identity_sessions** is embedded and applied after the unchanged
001_identity_initial, using the existing explicit --migrate path, advisory lock
and transactional ledger. It adds user update/lockout fields; refresh_families;
consumed/replacement metadata; family-owner/replacement foreign keys, uniqueness
and expiry checks; indexes and runtime grants. Existing sessions are backfilled,
not deleted. Inconsistent legacy ownership fails the migration transaction.
Runtime and migrations reject every database name except swiftdrop_identity.
Normal startup performs no DDL; no live/shared database was migrated.

Configure these privately (double underscores for environment variables):

| Setting | Required value |
| --- | --- |
| ConnectionStrings:Database | Restricted Identity runtime connection string, TLS for Neon |
| ConnectionStrings:MigrationDatabase | Existing separate Identity migration connection string with DDL/grant rights |
| Identity:Tokens:Issuer / Audience | Agreed HTTPS issuer and exact human API audience; no invented production values |
| Identity:Tokens:PrivateKeyPath / KeyId | Absolute external RSA private PEM path and stable key identifier |
| Identity:Tokens:PreviousPublicKeyPaths:{kid} | Optional external public PEMs retained across signing-key rotation |
| Identity:Google:WebClientIds / AndroidClientIds | Explicit configured client audience arrays; no client secret |
| Identity:Browser:AllowedOrigins | Exact canonical same-site HTTPS frontend origins; no wildcard |
| Identity:Proxy:KnownProxies / ForwardLimit | Explicit immediate/chain proxy IPs; bounded hop count 1–5, default 1 |

Signing keys load lazily and fail closed; liveness remains available without
keys/database. /ready and registration report safe 503 for missing/unreachable,
foreign or malformed database configuration. Key rollover uses a new kid/key,
retains previous public keys through token expiry/skew, and restarts Identity to
load the configured ring. Downstream consumers still need their agreed issuer,
audience, JWKS cache and unknown-kid policy. No Delivery authentication code is
changed here.

Safe development-key provisioning, from the backend root (task-local SDK must
be on PATH where the pinned SDK is not installed globally):

```powershell
$identityKeyDirectory = Join-Path $env:LOCALAPPDATA 'SwiftDrop\Identity'
New-Item -ItemType Directory -Force -Path $identityKeyDirectory | Out-Null
$env:Identity__Tokens__PrivateKeyPath = Join-Path $identityKeyDirectory 'development-20261026.pem'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/Services/Identity/SwiftDrop.Identity.Api --configuration Release --no-launch-profile -- --generate-development-key
```

This explicit command creates RSA-3072 material outside Git, refuses overwrite,
and never prints the key. Production/relative/Git/linked paths are rejected.
Unix creation uses 0600; Windows inherits the selected private directory's ACL.
Supply the remaining issuer/audience/kid/origin/audience-ID settings separately.
Keep signing and ASP.NET Data Protection keys outside images/Git. Persist/share
the Data Protection ring where browser sessions must survive restarts/replicas;
otherwise those protected cookies require fresh login. SameSite=Strict is not
weakened for cross-site deployments or HTTP debugging. A trusted HTTPS ingress
and correct proxy topology are prerequisites for real browser/mobile testing.

## Files and verification

Backend only:

- Application: IdentityAuthenticationContracts.cs and IdentityAuthenticationService.cs.
- Infrastructure: Argon2PasswordHasher.cs, IdentitySchemaMigrator.cs,
  PostgresUserRegistrationStore.cs, new PostgresIdentityAuthenticationStore.cs,
  migration 002, Authentication/{IdentityAccessTokens,
  IdentityTokenAuthenticationExtensions, IdentityProxyExtensions,
  DevelopmentSigningKey}.cs and Google/GoogleIdentityVerifier.cs.
- Presentation: IdentityEndpoints.cs and BrowserAuthentication.cs; Program.cs
  wiring and Identity csproj package/resource references.
- Identity tests: updated FoundationTests/RegistrationTests; new adapter,
  Google, session, browser, proxy, readiness, development-key, private PostgreSQL
  fixture/persistence tests and IdentityApiFactory.
- scripts/verify-foundation.ps1: only Identity expectations/config isolation
  updated for POST login and fail-closed JWKS. Delivery checks are preserved.
- This backend-local report. No source-document, infra, React/Flutter, Delivery,
  Merchant, Worker, Gateway or shared-contract implementation changed.

Observed verification on 26 October 2026, using .NET SDK 10.0.301:

| Check | Actual result |
| --- | --- |
| `dotnet restore SwiftDrop.sln` | Passed; package auditing remained enabled |
| `dotnet build SwiftDrop.sln --no-restore --configuration Release` | Passed: 0 warnings, 0 errors |
| `dotnet test SwiftDrop.sln --no-build --no-restore --configuration Release --environment Logging__EventLog__LogLevel__Default=None` | Passed: **663**, failed: **0**, skipped: **0** |
| Identity test assembly | **212 passed**: adapter/cryptography, real PostgreSQL migrations/persistence/concurrency, HTTP sessions/Google/browser/proxy/readiness and key provisioning |
| Other test assemblies | Delivery **372**, Gateway **68**, Merchant **6**, Notification **5** passed |
| `scripts/verify-foundation.ps1` | Passed: 11 project boundaries, 5 hosts, 14 missing-route probes, Identity login/JWKS/readiness expectations and existing Delivery checks; owned hosts stopped |
| Scoped `dotnet format` and verification | Passed for Identity source/tests; no unrelated formatting changes |
| `dotnet list src/Services/Identity/SwiftDrop.Identity.Api/SwiftDrop.Identity.Api.csproj package --vulnerable --include-transitive` | Passed; NuGet reported no vulnerable direct/transitive packages |
| `git diff --check` | Passed |

The browser refresh-cookie substitution regression first reproduced the defect
(expected 400, actual 200), then passed after protecting and binding the cookie
envelope. The focused browser suite passed **15/15** and the final full solution
run above includes that fix. Signature tests exercise real RSA/JWKS validation;
Google verification uses signed isolated fixtures with the supported library,
not live Google accounts. Database tests use owned, disposable PostgreSQL 18.1
clusters and runtime-role grants; the shared local database service is untouched.

Docker container verification could not run because the Linux engine named pipe
was unavailable. Hosted CI, live Neon, real Google SDK/device sign-in and deployed
TLS ingress/cookie behavior were not executed. These remain integration checks,
not successful results. No production signing key, credentials or generated
build artifacts are included in the commit.

## Documentation differences and next phase

The original security/REST/sequence contracts are followed for token exchange,
roles, session rotation, logout, explicit linking and bounded JWT expiry. Exact
proposed amendments for the source docs are: add GET /api/v1/auth/csrf with its
csrfToken/googleNonce output; X-CSRF-Token and protected host-cookie binding;
the browser/native transport inference and native nonce limitation; absolute
family expiry; new physical family/consumption fields; explicit proxy trust and
configuration/key setup. Source docs were not silently edited.

The baseline's broader architecture approval remains pending. Its stale
deployment/frontend scaffold prose does not reflect the new endpoints. The
Identity ER is logical and omits the new physical rotation/family/lockout fields.
The separate runtime/migration login rule is honored for Identity; the previously
recorded Delivery credential contradiction is unchanged and outside this task.

Outstanding integration dependencies are provisioned Neon roles/migrations,
actual signing/JWKS/Google audiences, same-site TLS ingress/proxy and Data
Protection storage, real React/Flutter SDK nonce/cancellation/storage flows,
and downstream JWT validation. Docker and hosted CI require separate execution.
No live Google login, deployed TLS-cookie flow, live Neon readiness or immediate
downstream JWT revocation is claimed.

Recommended next phase: **Part A Phase 5**, profile management and Admin
user/role/deactivation operations with last-active-Admin protection. Broader audit
retrieval remains Phase 6; client UI integration remains Phase 8. Do not continue
automatically. Commit the focused Identity work locally; no push, merge or PR
creation is performed.
