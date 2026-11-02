# Identity human-token integration contract

This Part A Phase 7 handoff records the actual Identity issuer and an isolated
cryptographic integration harness. It does not enable production Delivery
authentication, mint workload credentials or claim a deployed integration.
The source authentication and authorization specifications remain Proposed;
their team approval and environment values have not been inferred.

## Reinspected baseline and teammate work

On 26 October 2026 the backend working tree was clean on
`feat/identity-sessions-google` at `cec46e8` before this task. Its preceding
Identity report records a Release build with zero warnings/errors, 212 Identity
tests and 663 total solution tests passing. Current execution results belong in
the phases 5–9 report, separately from those historical results.

Fetching backend refs found `origin/main` at `884f576`, eleven commits ahead of
the old shared base `32604d4`. That work includes Delivery reliability tests,
authentication-boundary hardening, client harnesses and Merchant compatibility.
No merge, rebase, cherry-pick or edit of those production modules is part of this
Identity work. The remote handoff
`docs/delivery-identity-integration-handoff.md` and the hardening of
`ClaimsCurrentCustomer` were reviewed without copying or overwriting them.

The local and remote normal Delivery hosts still register
`UnconfiguredIdentityHandler`. They deliberately reject human credentials
until a verified adapter is integrated. `ClaimsCurrentCustomer` interprets an
already authenticated principal; it does not verify JWTs. Delivery quote and
booking endpoints require authorization and preserve their own ownership rules.

## Implemented wire contract

| Item | Actual Identity behavior |
| --- | --- |
| Signature/header | RSA RS256; `typ=JWT`; nonempty configured `kid`; RSA key at least 2048 bits |
| `sub` | Internal immutable user UUID in canonical `Guid.ToString("D")` form; not a Google subject, email or client-supplied owner |
| `roles` | JSON array of exact, case-sensitive `Customer`, `Courier`, `Merchant`, `Dispatcher`, `Admin` strings from Identity associations; an empty array grants no domain role |
| `iss` | Exact externally supplied HTTPS issuer, `Identity:Tokens:Issuer` |
| `aud` | One exact configured human API audience, `Identity:Tokens:Audience` |
| `iat`/`nbf` | Equal Unix-second values set at issuance |
| `exp` | `iat + 900` seconds |
| `jti` | New random unique token identifier per issuance |
| Contact/credential fields | No password, password hash, email, phone, address, refresh token or Google token in access claims |
| Key endpoint | Public `GET /.well-known/jwks.json`, exact public `kty`, `use`, `alg`, `kid`, `n`, `e` fields only |
| Key overlap | Configured current public key plus explicitly retained previous public keys; no private key is sent downstream |
| Credential separation | Google ID token is input only to login/link; Merchant API keys and workload audiences cannot authorize human Customer operations |

The environment issuer, audience, trusted JWKS origin and operational key IDs
must be supplied by the team. The harness values `https://identity.example.test`
and `swiftdrop-api` are isolated test configuration, not agreed deployment values.
The service is not a complete OIDC authorization server and exposes no invented
discovery, callback, code exchange or public workload-token endpoint.

## Downstream verifier and adapter requirements

An independently configured service must validate the signature against keys
obtained only from the trusted configured JWKS endpoint; require RS256, the
exact issuer and single human audience, `typ=JWT`, expiration and the required
human claims. It must reject an unknown/missing `kid` instead of trying arbitrary
keys. Preserve raw `sub` and map each `roles` array member as a role claim:

```csharp
options.MapInboundClaims = false;
options.TokenValidationParameters.NameClaimType = "sub";
options.TokenValidationParameters.RoleClaimType = "roles";
options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
options.IncludeErrorDetails = false;
```

These assignments are only the mapping portion of a complete verifier. They do
not by themselves validate a credential. Identity's own protected endpoints
also check one nonempty UUID subject, `jti`, `iat`/`nbf`/`exp`, equal issue/start
time, at most 900 seconds of issued lifetime and an explicit array containing only
known role strings. An active human with an empty role array remains authenticated
for self-profile operations, but receives no Customer/Admin/Courier privileges. Delivery's
owner-reviewed hardened principal adapter on remote main requires exactly one
authenticated identity and exactly one subject from that identity. Its future
authentication wiring must preserve those constraints.

No forwarded user/role header, API key, request body ID, Google provider subject,
or unauthenticated identity can replace the verified UUID. A valid human without
the required role receives 403; absent/invalid credentials receive safe 401;
foreign resource access retains the owning API's 404 policy. Delivery continues
to enforce quote/booking ownership and idempotency itself, without Identity SQL.

The [service catalog](../../swiftdrop-docs/architecture/service-catalog.md) requires
a five-second JWKS retrieval deadline. The
[authentication specification](../../swiftdrop-docs/security/authentication.md)
requires cached keys for
at most one hour, one bounded unknown-key refresh and fail-closed behavior after
an unsuccessful refresh. Production retrieval/cache/refresh middleware is not
implemented by this test harness and must be integrated and tested by the
downstream owner. Configure a trusted origin rather than token-controlled
`jku`/`x5u` URLs; never fall back to unsigned tokens or caller-supplied public keys.

Signing-key rotation mounts a new private key/kid into Identity, retains previous
public keys through the last issued token's expiry and skew, and restarts Identity
to load the ring. Downstream caches need the documented bounded refresh policy.
Removing an old public key immediately is not compatible with retaining still
valid tokens unless a separate revocation policy has been approved.

## Isolated executable proof

`tests/SwiftDrop.Identity.Tests/CrossServiceTokenTests.cs` references both service
assemblies from the test project only. Production API project dependencies
remain separate. It uses:

1. Real Identity HTTP registration and password login against the owned private
   PostgreSQL fixture, without a fake authenticated-principal handler.
2. The actual Identity HTTP JWKS response, containing public key data only.
3. Standard ASP.NET Core JwtBearer middleware configured only within a Delivery
   `WebApplicationFactory` test host. Its resolver uses that public JWKS snapshot;
   it never reads Identity SQL or signing keys to authenticate a request.
4. The unchanged Delivery quote endpoint, `ClaimsCurrentCustomer`, estimator and
   domain quote. A recording in-memory `IQuoteStore` captures the resulting owner
   without connecting to Delivery's database or modifying bookings.

The valid issued Customer token produces a quote with the same internal UUID,
and has neither Admin nor Courier authorization. Forged user/role headers do not
alter that owner or privilege. Wrong issuer, audience (including trailing slash
or extra audience), workload audience, signature, algorithm, time, required
claims or signing key fail before any quote-store read/write. Unsigned tokens and
payload role tampering fail. A correctly signed non-Customer token receives 403.
Headers, API keys and an untrusted Google-token-shaped bearer cannot establish
human identity. Error projections are checked for credentials, personal data,
private keys and raw verifier exception details.

The rotation case starts a separately configured Identity test host with a new
signing key and retained previous public PEM. Its actual two-key JWKS verifies
both a pre-rotation login token and a newly issued token at the unchanged
Delivery entry. Test-only signing material is generated outside Git, is never
printed and is deleted after the fixture. Signed malformed-claim fixtures may
read their own isolated test private key to construct negative inputs; the
downstream validation path still receives public keys only.

Run from the backend root with the pinned .NET SDK and PostgreSQL binaries:

```powershell
dotnet test tests/SwiftDrop.Identity.Tests/SwiftDrop.Identity.Tests.csproj --configuration Release --filter FullyQualifiedName~CrossServiceTokenTests --environment Logging__EventLog__LogLevel__Default=None
```

Observed on 26 October 2026 with SDK 10.0.301 and the owned PostgreSQL 18.1
fixture: Release compilation succeeded and the focused batch passed **43/43**
tests, **0 failed, 0 skipped**, in 51 seconds. That batch contains these **31**
cross-service cases and **12** PostgreSQL authentication-audit cases. The command
used the filter `FullyQualifiedName~AuthenticationAuditTests|FullyQualifiedName~CrossServiceTokenTests`.
These are contract-harness results, not a claim that production Delivery
authentication has been enabled.

This proof does not test live network/TLS routing, Gateway forwarding, live JWKS
retrieval/cache deadlines, a retired-key deployment or actual Delivery database
side effects. Existing Delivery integration suites cover its own persistence.
Production Delivery remains fail closed; the test-only scheme cannot be enabled
by a deployment setting or request header.

## Remaining contract differences and integration dependencies

- **Expiry bound:** source authentication permits 900-second lifetime and up to
  30-second clock skew, but authentication/authorization also promise role and
  deactivation changes within at most 15 minutes. The current effective downstream
  acceptance bound may therefore be 930 seconds. No immediate downstream JWT
  revocation, or strict effective 900-second bound, is claimed. The team must
  reconcile the exact required bound before deployment; this harness preserves
  the existing issuance contract.
- **Infrastructure setting names:** current infra templates use `Jwt__Issuer`,
  `Jwt__Audience`, `Google__WebClientId` and `Google__AndroidClientId`. Identity's
  actual settings are `Identity__Tokens__Issuer/Audience`, external
  `Identity__Tokens__PrivateKeyPath/KeyId`, and explicit
  `Identity__Google__WebClientIds/AndroidClientIds` arrays. Templates also omit the
  browser/proxy/Data Protection key setup and still use process `/health` as
  readiness rather than database `/ready`. No live readiness is established by
  those templates.
- **Owner activation:** named Identity/security/Delivery owner approvals and
  environment configuration are unallocated in source documentation. No message
  was sent to teammates and no coordination approval is invented. Supply the
  concrete issuer/audience/JWKS values and agree a narrowly scoped downstream
  auth integration that preserves the remote principal hardening and unchanged
  quote/booking application contracts.
- **Workloads:** Merchant/Worker scoped service JWT provisioning, internal gRPC
  authentication and complete Merchant booking flows remain outside Part A.
  There is no public service-token minting endpoint or Customer service privilege.
- **CI and live systems:** backend CI runs restore, Release build, solution tests
  and the process/boundary smoke script on Ubuntu with pinned SDK 10.0.301.
  PostgreSQL fixtures require actual installed binaries and fail instead of
  skipping if unavailable; hosted runner execution remains separate evidence.
  Neon, deployed ingress/TLS, real Google SDKs and operational key rotation still
  require their actual external configuration and verification.

No source documentation, infrastructure template, Delivery production handler,
booking implementation or other service's migrations are edited by this proof.
