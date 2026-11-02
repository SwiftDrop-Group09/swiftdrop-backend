# Part A: profile, administration, audit and integration verification

> Historical checkpoint: 26 October 2026. The 27 October continuation implements
> Flutter Identity and adds real HTTPS client integration checks; see
> [the continuation report](identity-client-integration-2026-10-27.md).

## Assessment and scope

Reinspected on 26 October 2026. Backend started clean on
`feat/identity-sessions-google` at `cec46e8`. The previous implementation was
verified again before changing it: all **663 solution tests**, including **212
Identity tests**, passed. Registration, Argon2id credentials, signed JWT/JWKS,
Google verification/linking, browser CSRF and hashed rotating refresh families
were reused.

Fetching found backend `origin/main` advanced to `884f576`, eleven commits after
the earlier common base. Its Delivery authentication-boundary, reliability,
client harness and Merchant compatibility work was reviewed without merging,
rebasing or modifying it. Both its Delivery host and this branch still use
`UnconfiguredIdentityHandler`; booking implementation does not imply that a
production Identity JWT verifier has been activated. Docs remain clean at
`d97fff4`; infra at `075a8b4`. React main `c062b11` and Flutter main `e9778c1`
and their separate teammate booking branches were inspected before client work.

Authoritative REST, authorization/authentication, SRS/traceability/use cases,
logical ER, deployment and test documents remain Proposed. Their approval has
not been inferred. Current Npgsql/embedded SQL and application ports were
extended; no competing Identity framework, direct downstream SQL access or new
shared production library was introduced.

The missing pieces were profile operations, Admin management, race-safe
last-active-Admin protection, audit retrieval and client integration. The plan
was to add transactional Identity persistence and transport, test it against
private PostgreSQL, prove the existing JWT wire contract with an isolated
downstream harness, then integrate React and record exact Flutter prerequisites.

## Phase 5: implemented account management

| Route | Contract and behavior |
| --- | --- |
| GET /api/v1/users/me | Authenticated active user; own safe Profile only |
| PUT /api/v1/users/me | fullName, nullable phoneNumber, optional nullable address; own subject from verified JWT |
| GET /api/v1/users | Current active Admin; ProfilePage |
| POST /api/v1/users | Current active Admin; email, fullName, isActive, create-only password; 201 safe Profile, initial Customer role only |
| PUT /api/v1/users/{id} | Current active Admin; email, fullName, isActive; 200 Profile; no password or role fields |
| DELETE /api/v1/users/{id} | Current active Admin; soft deactivation and all stored refresh families/sessions revoked atomically; 204 |
| PUT /api/v1/users/{id}/roles | Current active Admin; exact system role names; atomic replacement and 200 Profile |

No undocumented GET /users/{id} or arbitrary-user self-profile route was added.
Profile updates require the fullName and phoneNumber fields (phone may be null);
address is optional. Profile keeps the established id/email/fullName/phoneNumber/address/roles/isActive
projection. Password hashes, session data, provider subjects and internal security
fields never appear. Names are trimmed, nonblank and at most 120 characters;
phone is at most the existing 32-character schema limit; empty phone/address
clear the field. Email is validated, trimmed, normalized and uniquely indexed.
Bodies are bounded to 16 KiB, with strict types/case, duplicate/unknown field
rejection. Page queries accept only page (default 1, minimum 1) and pageSize
(default 20, 1–100); lists order ascending createdAt+id and use a repeatable-read
snapshot for count and items. No undocumented search/filter semantics were added.

The user explicitly approved the create-only `password` field to resolve the
missing credential transport contract. An Admin supplies a privately generated
12–128-character password; Identity hashes it with the existing Argon2id adapter.
It is absent from responses, updates, audits and logs. No password-email delivery
service or password-reset mechanism is claimed. Duplicate normalized email
returns generic 409 account_conflict. Roles are assigned separately after creation.

JWT Admin role authorization is followed by a current database active/Admin
check on every management operation. Stale tokens cannot administer after
demotion/deactivation. Self-profile also checks current activation. Admin writes
take one transaction advisory lock before deterministic actor/target user locks,
so concurrent demotion/deactivation cannot remove both remaining Admins. Profile
and authentication transactions keep the same user-before-family lock order.
All mutations and required audit writes commit or roll back together.

Roles may be empty for an active account. Identity then issues/verifies an
explicit empty roles array: the user can access its profile but has no Customer,
Courier or Admin authorization. Invalid or non-array role claims are rejected.
Deactivation prevents new login/refresh immediately and revokes all existing
refresh state; reactivation never restores revoked tokens. Downstream access
JWTs retain their existing expiration behavior.

Browser mutations require exact allowed Origin and the established protected
CSRF context. CORS now includes PUT/DELETE and safe correlation headers. Management
requests are limited to 60/minute per verified subject. Invalid credentials
return 401; insufficient privileges 403; missing target 404; conflicts or
last_active_admin 409; malformed input 400; dependency errors safe 503.
ProblemDetails and response headers carry an opaque UUID correlation ID, generated
when absent/invalid. Arbitrary caller text is never copied into audit correlation
fields. It is an identifier, never an authentication credential.

## Phase 6: implemented audit capability

GET /api/v1/audit-logs requires a current active Admin and returns only
`items[{id,actorId,action,resourceId,timestamp,resultCode}],totalCount,page,pageSize`.
It exposes no bodies, contact information, passwords, token hashes, provider
tokens/subjects or signing material. Existing physical BIGINT audit IDs and role
names are retained rather than silently replacing the established schema.

Successful modifications record ProfileUpdated, UserCreated, UserUpdated,
RolesChanged and UserDeactivated, with actor/target IDs and server time in the
same transaction. Existing UserRegistered and GoogleLinked records are retained.
Authentication lifecycle events now include LocalLogin, GoogleLogin,
SessionRefreshed and Logout; known local reauthentication failures, refresh reuse
and disabled refresh record fixed Rejected result codes. Audit failures roll back
the corresponding state change, including refresh consumption and revocation.

This is an Identity audit facility. Unauthenticated unknown-account/provider
rejections and rejected Admin commands have no invented durable audit policy.
Their ordinary safe HTTP errors remain unchanged. Retention/purge policy and
cross-service audit aggregation are unresolved and no cleanup job deletes data.

Migration **003_identity_administration** preserves historical audits, backfills
result_code=Success, adds bounded nullable correlation_id and safe-value checks,
adds stable list indexes, and grants only the required DELETE on user_roles.
Runtime cannot delete users, update/delete audit records or insert arbitrary
roles. Migration 001/002 are unchanged; readiness now requires 003. Apply through
the existing explicit --migrate operation with the separate migration credential.
No live/shared database was migrated.

An explicit offline **--provision-development-admin** command supports local
Admin setup without a public bootstrap endpoint. It requires Development,
externally supplied Identity:DevelopmentAdmin:Email/FullName/Password, the exact
Identity runtime database and migration 003. It creates a new Customer/Admin and
audit atomically, never promotes/resets/reactivates an existing account, prints
no credentials and performs no DDL. Supply secrets through a private process
environment or secret provider, not committed settings or command-line literals;
remove the password setting after execution. Never use this helper for production
provisioning. Combining provisioning modes is rejected.

With the runtime database connection and private DevelopmentAdmin settings
already supplied by the operator's secret provider/process environment, run from
the backend root:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/Services/Identity/SwiftDrop.Identity.Api --configuration Release --no-launch-profile -- --provision-development-admin
```

Environment names use double underscores, for example
Identity__DevelopmentAdmin__Password; do not put its value in shell history.

## Phases 7 and 8: integration status

[Cross-service contract](identity-cross-service-contract.md) documents the exact
UUID sub, roles array, RS256, issuer/audience, JWKS, rotation and expiry contract.
An executable test-only harness uses real Identity HTTP registration/login and
HTTP JWKS with standard JwtBearer middleware in an unchanged Delivery test host.
The resulting quote has the same Customer UUID; invalid/tampered/workload tokens
and role impersonation fail before storage. Retained old/new public keys are
tested. Only the test project references both services; production service
references remain independent.

**Production Delivery activation remains an owner integration dependency.** The
harness uses a public JWKS snapshot, not an operational retrieval/cache service.
Trusted JWKS origin, one bounded unknown-key refresh, retrieval deadline, cache
expiry and deployment values need downstream implementation/verification. No
booking handlers, Delivery migrations or workload-token provisioning were changed.

React has one in-memory Identity session/provider and authenticated request seam,
local register/login, Google SDK nonce flow and explicit linking, profile update,
refresh/logout and Admin account/role/deactivation/audit screens. It reuses current
Router, Query, forms, Zod and testing tools without new package dependencies.
The create form generates passwords through Web Crypto and requires confirmation;
privileged changes also require confirmation. Browser refresh tokens remain
HttpOnly cookies; no access/refresh token or password is placed in local/session
storage. Tests use isolated API/SDK fixtures; live Google and real TLS-cookie
execution are not claimed. See the frontend-local Identity integration handoff
for its complete files, commands, teammate booking request seam and TLS setup.

**Flutter Customer/Courier integration is blocked**, as Phase 8 permits for
missing client infrastructure. Main remains the counter scaffold; its README
requires team agreement on navigation/state/networking dependencies. Flutter/Dart
are unavailable here. The teammate booking branch has separate CustomerSession
and RequestTransport seams, but these were not merged or rewritten. The mobile
handoff records exact endpoints, secure-storage and Google SDK requirements,
nonce limitation, refresh concurrency, role checks and required device tests.
Courier role alone must never create an operational Delivery courier profile.

## Phase 9: verification

Observed final results on 26 October 2026:

| Check | Actual result |
| --- | --- |
| .NET SDK | 10.0.301, matching the pinned repository SDK |
| `dotnet restore SwiftDrop.sln` | Passed; all projects restored/current, auditing enabled |
| `dotnet build SwiftDrop.sln --no-restore --configuration Release` | Passed: **0 warnings, 0 errors** |
| `dotnet test SwiftDrop.sln --no-build --no-restore --configuration Release --environment Logging__EventLog__LogLevel__Default=None` | **801 passed, 0 failed, 0 skipped** |
| Identity assembly | **350 passed**; includes actual PostgreSQL, HTTP, authentication/authorization, concurrency, audit rollback, cryptography and downstream harness tests |
| Other assemblies | Delivery **372**, Gateway **68**, Merchant **6**, Notification **5** passed |
| Scoped `dotnet format ... --verify-no-changes` | Passed for Identity source/tests |
| `scripts/verify-foundation.ps1` | Passed project boundaries, five-host startup, Gateway routing, Identity readiness/JWKS behavior and existing fail-closed Delivery checks; owned hosts stopped |
| `dotnet list SwiftDrop.sln package --vulnerable --include-transitive` | NuGet reported **no vulnerable packages in all 11 projects** |
| React `npm run check`, pinned Node 24.16.0/npm 11.13.0 | Passed Prettier, ESLint (0 warnings), strict TypeScript, **46 tests in four files**, production SPA/root-prerender build |
| React `npm audit --omit=dev` | **0 vulnerabilities reported** |
| `git diff --check` | Passed for changed repositories |

The final Identity suite includes 32 administration persistence cases, 37
administration HTTP cases, 31 cross-service cases, 12 authentication-audit cases
and 27 Development Admin provisioning cases, alongside the retained foundation,
session, browser, cryptographic and Google tests. Owned temporary PostgreSQL
18.1 clusters exercise real constraints, runtime grants, transaction rollback
and concurrency. No shared database was used or migrated. The Windows-only
EventLog logger is suppressed for test output; authentication is not bypassed.

An intermediate full run had one failure: the older wrong-password Google-link
test assumed no audit row. It was updated to assert exactly one safe Rejected
reauthentication event and unchanged account/provider/session state, then the
800-case run passed. A final contract check made the required phoneNumber field
explicit and added its omission regression; the final **801-case** run above
passed after that change. No earlier failure is being represented as a pass.

Docker verification could not run: the Docker Desktop Linux-engine named pipe
was unavailable. Flutter/Dart checks, hosted GitHub Actions, live Google SDK
consent, live Neon, deployed HTTPS-cookie/proxy behavior and production downstream
JWKS retrieval were **not executed**. Local commands corresponding to backend
and frontend CI were run; this is not a hosted CI success claim. No private
signing keys, real credentials, fixture passwords or generated build output
are included as production assets.

## Files changed and documentation differences

The file inventory is **23 backend files, 24 frontend files and one mobile
handoff**. Paths in the Identity source/test rows are relative to their stated
directories; every file is within the assigned Identity scope.

| Repository / directory | Modified or added files |
| --- | --- |
| Backend `src/Services/Identity/SwiftDrop.Identity.Api/Application` | IdentityAdministrationContracts.cs |
| Backend Identity `Infrastructure` | PostgresIdentityAdministrationStore.cs, PostgresIdentityAuthenticationStore.cs, IdentitySchemaMigrator.cs, Migrations/003_identity_administration.sql |
| Backend Identity `Infrastructure/Authentication` | DevelopmentAdminProvisioner.cs, IdentityAccessTokens.cs, IdentityTokenAuthenticationExtensions.cs |
| Backend Identity root/presentation | Program.cs, SwiftDrop.Identity.Api.csproj, Presentation/IdentityAdministrationEndpoints.cs |
| Backend `tests/SwiftDrop.Identity.Tests` | AdministrationApiTests.cs, AuthenticationAuditTests.cs, CrossServiceTokenTests.cs, DevelopmentAdminProvisionerTests.cs, FoundationTests.cs, GoogleApiTests.cs, SwiftDrop.Identity.Tests.csproj, Persistence/AdministrationPersistenceTests.cs, Persistence/IdentityPersistenceTests.cs |
| Backend script/reports | scripts/verify-foundation.ps1, docs/identity-cross-service-contract.md, docs/identity-phases-5-9.md |
| Frontend `app/features/identity` | admin-page.tsx, api.ts, api.test.ts, auth-page.tsx, components.tsx, contracts.ts, google-button.tsx, pages.test.tsx, profile-page.tsx, session.tsx, session.test.ts, test-fixtures.ts |
| Frontend routing/root | app/portals/admin/users.tsx, app/portals/auth/login.tsx, app/portals/auth/register.tsx, app/portals/customer/profile.tsx, app/portals/public/index.tsx, app/root.tsx, app/routes.ts |
| Frontend config/style/handoff | .env.example, README.md, app/env.d.ts, app/styles/app.css, docs/identity-integration.md |
| Mobile | docs/identity-handoff.md only |

The test csproj and smoke-script allowlist gain a test-only Delivery reference;
production service references remain unchanged. Source docs and infra are
untouched. No unrelated service implementation or teammate booking work was
modified. The [React handoff](../../swiftdrop-frontend/docs/identity-integration.md)
and [mobile handoff](../../swiftdrop-mobile/docs/identity-handoff.md) provide
client-specific configuration and verification details.

Exact proposed source-document amendments are the approved create-only password
field, initial Customer-only Admin-created account, empty-role semantics,
ascending paging direction, 32-character nullable phone, current active-Admin
checks, all-family revocation on deactivation, explicit audit action/result policy,
physical audit/correlation fields and controlled offline Development provisioning.
Audit search filters/retention and production first-Admin provisioning remain
unspecified. Earlier Phase 3/4 browser bootstrap amendments also remain applicable.

The source SRS's physical deletion wording conflicts with the canonical REST and
master's soft-deactivation rule; this implementation follows the latter. Logical
ER UUID role/audit IDs differ from existing physical role-name/BIGINT identifiers.
The advertised at-most-15-minute downstream revocation bound conflicts with the
permitted 900-second token plus 30-second validation skew: effective acceptance
can reach 930 seconds. No unilateral lifetime change or immediate revocation is
claimed. The retained authentication limiter is 30 requests/minute/IP across its
routes, while REST proposes 10/minute/IP for login/Google; that earlier-phase
limit still needs reconciliation. Infra's obsolete Jwt__/Google__ settings, missing private-key mounts,
liveness-only probes, stale frontend backend-availability prose and mobile
SignalR references require reconciliation by their owners.

Next work is integration acceptance: approve source-contract amendments, provision
Neon roles/migrations and environment keys/audiences, activate downstream JWT/JWKS
verification, configure same-site TLS/proxy/Data Protection, agree Flutter
infrastructure and execute real browser/device/Google tests plus hosted CI and
container verification. Phases 1–9 define no additional automatic implementation
phase. Suggested review units are backend management/audit, cross-service harness,
React identity and mobile handoff; no commits, pushes, merges or PRs are made.
