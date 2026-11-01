# Delivery authentication and Identity integration handoff

This is the Phase 5 handoff for Customer fee estimation and booking. It records
the actual backend baseline `8cf0e11` and documentation baseline `d97fff4`,
reinspected on 24 October 2026, together with the authentication boundary
hardening in this phase. It does not establish a completed Identity token
service, team approval, a deployed issuer or a successful cross-service JWT test.
No teammate was contacted and no allocation or approval is inferred.

## What exists

Delivery application handlers depend on
[`ICurrentCustomer`](../src/Services/Delivery/SwiftDrop.Delivery.Api/Application/ICurrentCustomer.cs),
which exposes authentication state, Customer authorization and a nullable UUID
owner. They do not read bearer tokens, request identity headers or Identity SQL.
The same owner binds quotes, quote confirmation and the idempotency scope.

Normal Production and Development hosts register
[`UnconfiguredIdentityHandler`](../src/Services/Delivery/SwiftDrop.Delivery.Api/Infrastructure/Authentication/UnconfiguredIdentityHandler.cs#L7).
It returns no authenticated identity and challenges with `WWW-Authenticate:
Bearer` (lines 16–22). The registration is explicit in Delivery
[`Program.cs`](../src/Services/Delivery/SwiftDrop.Delivery.Api/Program.cs#L33),
and no environment switch enables a fictional Customer. Protected quote and
booking requests therefore return safe 401 responses until real token
verification is integrated.

Positive HTTP tests use the test-project-only
[`DeliveryTestAuthentication`](../tests/SwiftDrop.Delivery.Tests/Authentication/DeliveryTestAuthentication.cs)
helper through
[`QuoteApiFactory`](../tests/SwiftDrop.Delivery.Tests/Quotes/QuoteApiFactory.cs).
Its fictional principal is supplied by test setup, never by a request header.
Named authentication cases use fixed fictional UUIDs; database scenarios may
provide their own fictional UUIDs for isolation. The handler clones the
configured principal for each request so mutations cannot leak between requests.
These tests exercise ownership, roles, validation and database behavior; they
do not establish signature verification or real Identity interoperability.

Identity currently implements registration, database readiness, migrations and
password hashing. Its
[`Program.cs`](../src/Services/Identity/SwiftDrop.Identity.Api/Program.cs#L63)
maps `/health` at line 63, `/ready` at line 65 and
`POST /api/v1/auth/register` at line 89, then runs at line 142. Registration
returns a profile at line 139, not an access/refresh token pair. No login,
token issuer, JWT verifier, JWKS handler or refresh-rotation workflow exists.
The Identity project references Argon2 and Npgsql, with no authentication
integration package or registration
([project file, lines 14–15](../src/Services/Identity/SwiftDrop.Identity.Api/SwiftDrop.Identity.Api.csproj#L14)).
Its appsettings files contain listeners and logging, without an issuer,
audience or signing-key configuration.

## Delivery principal policy

[`ClaimsCurrentCustomer`](../src/Services/Delivery/SwiftDrop.Delivery.Api/Infrastructure/Authentication/ClaimsCurrentCustomer.cs)
interprets an already authenticated principal. The Phase 5 policy is:

- Exactly one identity in the principal must be authenticated. Multiple
  authenticated identities are ambiguous and are rejected.
- Read subject and role claims only from that authenticated identity. Claims
  attached to an unauthenticated identity cannot supply ownership or a role.
- That identity must contain exactly one `sub` claim, parsing as a nonempty
  `Guid`. Missing, malformed, empty or duplicate subjects cannot authorize an
  operation. The adapter does not impose a new UUID serialization format beyond
  the existing UUID parser. It does not fall back to `NameIdentifier`; the future
  JWT verifier must preserve or explicitly normalize the verified raw `sub`.
- Customer authorization requires the exact case-sensitive value `Customer`
  in the identity's configured `RoleClaimType` or a raw `roles` claim.
  A JSON array encoded as one claim string is not parsed here; the future JWT
  verifier must map the agreed roles representation explicitly.
- This adapter performs no JWT parsing, signature verification, issuer/audience
  validation or key retrieval. A principal must first be established by trusted
  authentication middleware. External headers cannot establish it.

Authentication and role checks precede input parsing and database resolution.
Missing or invalid identity returns 401; a valid authenticated subject without
the Customer role returns 403. Missing and foreign quotes return the same 404.
An authenticated identity does not authorize use of another Customer's quote
or idempotency result. These rules preserve the
[authorization contract, lines 5–9 and 30](../../swiftdrop-docs/security/authorization.md#L5).

## Implemented, proposed and unresolved contract

The authentication/security documents explicitly remain Proposed. Requirements
below describe the existing design, not an approval or implementation claim.

| Item | Verified implementation | Proposed contract and unresolved handoff |
|---|---|---|
| User identifier and `sub` | Identity creates internal UUIDs with `Guid.NewGuid()`; [registration store line 37](../src/Services/Identity/SwiftDrop.Identity.Api/Infrastructure/PostgresUserRegistrationStore.cs#L37). `users.id` is UUID; [migration line 10](../src/Services/Identity/SwiftDrop.Identity.Api/Infrastructure/Migrations/001_identity_initial.sql#L10). Delivery requires one nonempty UUID `sub`. | Confirm that issued `sub` is this stable internal user ID and agree its wire serialization. The docs list `sub` without defining its exact format; [authentication line 20](../../swiftdrop-docs/security/authentication.md#L20). Google provider subjects are separate external identifiers linked to an internal user; [Identity migration lines 31–36](../src/Services/Identity/SwiftDrop.Identity.Api/Infrastructure/Migrations/001_identity_initial.sql#L31). They cannot substitute for the Delivery owner UUID. |
| Issuer | No runtime issuer or token signer exists. | Exact issuer is environment-specific and unresolved; [authentication line 20](../../swiftdrop-docs/security/authentication.md#L20), [environments line 42](../../swiftdrop-docs/deployment/environments.md#L42). The docs' `identity.example.invalid` value is a placeholder, not an approved deployment value. |
| Human audience | No issued or verified audience exists. | Agree the exact human audience. The docs template's `swiftdrop-api-local` is an example, not evidence of an operational issuer/audience pair. Human and workload credentials remain separate. |
| Signature and required claims | No Identity JWT implementation exists. | RS256, explicit issuer/audience, `sub`, `roles`, `iat`, `nbf`, `exp`, `jti` and optional authenticated merchant membership; independently validate signature, algorithm, issuer, audience and time in each service. This is Proposed at [authentication line 20](../../swiftdrop-docs/security/authentication.md#L20). Do not accept unsigned tokens, Google ID tokens or Merchant API keys as Customer bearer credentials. |
| Customer roles | Registration assigns exact `Customer`; [registration store lines 58–65 and 81](../src/Services/Identity/SwiftDrop.Identity.Api/Infrastructure/PostgresUserRegistrationStore.cs#L58). Delivery supports mapped role claims or exact raw `roles=Customer` on the single authenticated identity. | Agree the JSON roles shape and explicit JWT-to-claims mapping, including subject/name mapping. Documentation names `roles` but does not supply an implemented token example. Do not infer Customer from email, phone, headers or arbitrary supplied IDs. |
| JWKS endpoint | No Identity JWKS handler exists. Gateway has an [explicit forwarding route, lines 96–100](../src/Gateway/SwiftDrop.Gateway/appsettings.json#L96). Routing does not supply keys or prove authentication. | Public-key-only `/.well-known/jwks.json`, trusted configured origin, no invented OIDC discovery/code endpoints; [authentication line 22](../../swiftdrop-docs/security/authentication.md#L22). Public key fields are `kty`, `use`, `alg`, `kid`, `n`, `e`; [REST line 125](../../swiftdrop-docs/api/rest-api.md#L125). Confirm reachable trusted origin and key provisioning before implementation. |
| JWKS timeout and rotation | No key cache, signing key or rotation workflow exists. | Five-second retrieval deadline; [service catalog line 38](../../swiftdrop-docs/architecture/service-catalog.md#L38). Cache trusted JWKS up to one hour, allow one bounded unknown-`kid` refresh, retain old verification keys through token expiry and fail closed for unknown keys; [authentication line 22](../../swiftdrop-docs/security/authentication.md#L22). Exact provisioning, rotation overlap and failure behavior need an implemented, tested agreement. |
| Access expiry and clock skew | No issuer/verifier enforces a token lifetime yet. | Human lifetime ≤900 seconds and explicit skew ≤30 seconds; [authentication line 20](../../swiftdrop-docs/security/authentication.md#L20). Agree the configured skew and effective acceptance/revocation bound; do not silently use a framework default. |
| Refresh and revocation | A refresh-session table exists; [Identity migration lines 39–46](../src/Services/Identity/SwiftDrop.Identity.Api/Infrastructure/Migrations/001_identity_initial.sql#L39). No refresh/logout endpoint or family-rotation logic exists. | Opaque ≥256-bit tokens, hashed storage, 30-day expiry, atomic family rotation/reuse rejection and own-family logout; [authentication lines 24–26](../../swiftdrop-docs/security/authentication.md#L24). Delivery does not own sessions or query Identity SQL. Existing access tokens have bounded expiry, not immediate downstream revocation. |
| Workload authentication | No Merchant/Worker token provisioning or Delivery internal adapter is implemented here. | Separate audiences/scopes and ≤5-minute lifetime; [authentication line 20](../../swiftdrop-docs/security/authentication.md#L20), [authorization line 28](../../swiftdrop-docs/security/authorization.md#L28). Do not grant Customer operations by accepting a service credential or Merchant API key. |
| Ownership and errors | Application handlers bind identity to stored quote ownership and principal/operation/key scope; production fails closed. | Customer JWT for estimate/confirm; [REST lines 28–29](../../swiftdrop-docs/api/rest-api.md#L28). Preserve 401/403/foreign-resource 404 and safe ProblemDetails; [REST line 171](../../swiftdrop-docs/api/rest-api.md#L171). |

The issuer, human audience, exact token subject/roles representation, key
provisioning and usable Identity endpoints must be supplied and verified before
enabling production JWT authentication. This handoff does not introduce runtime
settings, secret keys, secret values or an additional credential contract.

## Differences requiring reconciliation

The [security baseline, line 20](../../swiftdrop-docs/security/authentication.md#L20)
requires Gateway to strip untrusted `X-User-*` headers. Current Gateway
[`Program.cs`](../src/Gateway/SwiftDrop.Gateway/Program.cs#L9) and route
configuration provide forwarding without that stripping/authentication
implementation. Delivery ignores those headers and remains responsible for
authentication and ownership. A forwarding route and an internal network are
not evidence of verified identity; no Gateway authentication claim is made.

The proposed ≤900-second access lifetime plus permitted expiration skew
([authentication line 20](../../swiftdrop-docs/security/authentication.md#L20))
must be reconciled with the strict “at most 15 minutes” downstream
role/deactivation claim
([authentication line 26](../../swiftdrop-docs/security/authentication.md#L26),
[authorization line 30](../../swiftdrop-docs/security/authorization.md#L30)).
The final acceptance policy must state whether and how skew contributes to that
bound. No immediate revocation or strict effective bound is established by
the current implementation.

Identity's existing
[`FoundationTests.cs`, lines 25–33](../tests/SwiftDrop.Identity.Tests/FoundationTests.cs#L25)
expect GET login and JWKS paths to return 404. Those checks and the registration
tests are not token acceptance tests. The complete source route inventory,
rather than the GET login probe alone, establishes that POST login is absent.
The docs also describe future login/JWKS/session operations; their Proposed
status must remain distinct from actual registration functionality.

The [responsibility register, lines 3–14](../../swiftdrop-docs/project-management/team-responsibilities.md#L3)
has no confirmed named Identity/security owner. Record the future owner,
reviewer, agreed values and source-repository implementation evidence when
available; this document does not invent a contact, approval or completed
coordination step.

## Future cryptographic integration acceptance

Run these checks when a real Identity issuer and JWKS implementation exist.
They are pending cross-service acceptance, not results of the Phase 5
test-principal suite.

| Test | Required observation |
|---|---|
| Genuine Customer token | Valid Identity-issued token creates an owner-bound quote and confirms only that owner's quote; no fake principal/header handler participates. |
| Direct service and Gateway access | The same credentials, ownership and safe error rules apply; Gateway forwarding cannot bypass Delivery verification. |
| Invalid token credentials | Missing/malformed/tampered/unsigned tokens, wrong issuer/audience/algorithm, missing required claims and unknown keys return safe 401 before private database side effects. |
| Subject and roles | Missing/empty/malformed/duplicate UUID subjects fail; wrong role returns 403; supplied headers/body IDs cannot replace the token owner; the agreed roles representation maps without combining identities. |
| Time boundaries | Expired/not-yet-valid tokens, maximum lifetime and explicitly configured skew behave at documented boundaries. Record the effective role/deactivation expiry bound. |
| JWKS trust and availability | Retrieve keys only from the configured trusted origin within the bounded deadline; cache/refresh behavior does not accept unknown keys or turn retrieval failure into anonymous success. |
| Rotation | Old and new valid signing keys verify during the agreed overlap; unknown `kid` triggers only the bounded refresh; unknown/retired keys are rejected according to the agreed retention policy. |
| Credential separation | Google ID tokens, Merchant API keys and workload tokens cannot authorize Customer quote/booking operations. |
| Ownership and replay | Foreign quotes remain 404; idempotency results remain bound to the verified principal and operation across token refresh/rotation; failed authentication has no quote/parcel/history/snapshot/key/outbox effects. |
| Sessions and deactivation | Identity refuses fresh login/refresh after disable/revocation; downstream behavior matches the agreed access expiry/skew bound. Delivery never reads Identity's database. |

Replace the closed authentication registration with the approved real verifier
only after those concrete contracts and Identity implementation are available.
Retain test-only authentication for isolated automated tests and keep its
registration out of normal deployed hosts. No Identity login/issuance, Merchant
workflow, client session manager or cross-service database access belongs in
this handoff.
