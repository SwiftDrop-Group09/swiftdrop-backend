# Delivery Phase 5: independent authentication testing and Identity handoff

## Baseline and prerequisite gate

The backend was clean on `test/delivery-booking-reliability` at `8cf0e11`.
Main remains `32604d4`; Phases 1-3 are already merged and Phase 4 is included in
the new `fix/delivery-authentication-boundary` branch. No merge is necessary.
Docs `d97fff4`, infra `075a8b4`, frontend `c062b11` and mobile `e9778c1` were
reinspected read-only and remain unchanged. No AGENTS.md was found.

Before edits, contribution guidance, the original master Phase 5, current
security/REST/ownership and deployment contracts, booking diagrams, prior phase
reports, Delivery handlers/authentication/configuration/tests and actual Identity
implementation were reinspected. The assignment PDF remains available under
the docs requirements sources; this phase does not reinterpret its scope.

The prerequisite checkpoint was rerun: cached restore passed for all 11
projects; Release build had zero warnings/errors (8.31s); **470/470 tests passed**
with no failures/skips (Delivery 379 in 6m43s, Gateway 68 in 2m23s, Identity 12,
Merchant 6, Notification 5). EF reported no pending model changes. The working
tree stayed clean and no private PostgreSQL fixture directories remained.

## Assessment and file-level plan recorded before feature edits

`ICurrentCustomer` already isolates use cases from HTTP and caller-supplied
identity. Both endpoints and handlers authorize before validation/storage.
`UnconfiguredIdentityHandler` returns no identity in every normal environment;
test principals are registered only by the test project. Identity implements
Customer registration, but no token issuer, login or JWKS endpoint. Production
JWT integration is therefore unavailable; a replacement authentication system
and new configuration values are outside this phase.

One adapter gap needs correction: `ClaimsCurrentCustomer` checks authentication
on the principal's first identity, then searches subject and roles across all
identities. This can combine one identity's authentication with another's
claims, or silently select the first duplicate subject. The currently closed
production scheme does not expose this as a remote header/token bypass.

The planned file scope is:

| File | Purpose |
|---|---|
| src/Services/Delivery/SwiftDrop.Delivery.Api/Infrastructure/Authentication/ClaimsCurrentCustomer.cs | Select one authenticated identity; bind subject and role to it; reject ambiguous subjects/identities |
| tests/SwiftDrop.Delivery.Tests/Authentication/DeliveryTestAuthentication.cs | Reuse the existing fixed-principal handler with named deterministic fictional users, only in tests |
| tests/SwiftDrop.Delivery.Tests/Quotes/QuoteApiFactory.cs | Use that shared test fixture and permit explicit test-host environment selection |
| tests/SwiftDrop.Delivery.Tests/Authentication/ClaimsCurrentCustomerTests.cs | Exercise identity/subject/role isolation and fail-closed edge cases |
| tests/SwiftDrop.Delivery.Tests/Authentication/CustomerAuthenticationApiTests.cs | Verify normal Development/Production denial, test-only role checks and persisted ownership/replay |
| docs/delivery-identity-integration-handoff.md | Record actual/proposed/unresolved Identity details and later signed-token acceptance |
| README.md and this assessment | Explain current behavior, verification and the Phase 5 checkpoint |

Implementation order: adapter and test fixture, focused unit/HTTP/PostgreSQL
checks, full cached restore/Release build/solution regression, formatting, EF
consistency, actual host/Gateway smoke, final diff review and one conventional
commit. Other repositories/services, wire contracts, migrations, prices,
idempotency/outbox logic and client screens are outside the file scope.

## Identity dependencies and contradictions

The [Identity handoff](delivery-identity-integration-handoff.md) records the
source evidence. Issuer, human audience, token subject serialization, roles
representation and actual trusted JWKS origin remain unconfirmed. Documentation
proposes RS256, a public JWKS path, bounded token lifetime/skew and key rotation;
these are not implemented or newly approved here. SEC/Identity individual
ownership is unassigned, so no teammate confirmation or external contact is
claimed.

The documented strict 15-minute downstream revocation bound needs reconciliation
with permitted expiry skew. Documentation also calls for Gateway header
stripping that the actual Gateway does not implement. Delivery must continue
ignoring identity headers regardless of Gateway behavior. Earlier operational
contradictions remain recorded in the Phase 4 assessment; no unrelated docs or
infrastructure are rewritten.

## Implementation and verification

The adapter now selects exactly one authenticated identity and reads all owner
and role claims from that identity. Multiple authenticated identities fail
closed. Exactly one nonempty UUID `sub` is required for an owner; duplicate
subjects, including equal values, cannot select an owner. Existing GUID parser
forms remain compatible. Customer role values stay exact and case-sensitive,
using the selected identity's RoleClaimType or raw `roles` claims. No token or
header parsing is added to this adapter.

The existing fixed-principal test handler moved into
`DeliveryTestAuthentication`, which creates fresh principals using named fixed
fictional Customer/other-Customer/non-Customer IDs. It clones the configured
principal per request and never reads credentials from headers. Existing
database scenarios can still pass separate fictional IDs for isolation.
`QuoteApiFactory` delegates to that fixture and supports explicit environment
selection only inside the test project. No production reference to the test
assembly, fake-user switch or runtime authentication setting exists.

New authentication coverage consists of **56 test cases**:

| Coverage | Cases and evidence |
|---|---|
| Claims adapter | 37 cases: missing/anonymous context, supported role claim types, exact roles/multiple roles, missing/invalid/duplicate subjects, unchanged GUID forms, no NameIdentifier fallback, unauthenticated secondary identities in either order, multiple authenticated identities and forged request headers |
| Normal hosts | 4 Development/Production x quote/booking cases keep the closed scheme, omit the test scheme and return safe 401/Bearer despite spoofed identity headers, unsigned tokens, hostile unknown development settings and malformed private input |
| Wrong roles | 6 quote/booking cases for Merchant, Courier and Admin return 403 despite Customer spoofing, before body parsing/storage |
| Ambiguous claims over HTTP | 8 quote/booking cases reject duplicate subjects, multiple authenticated identities and subjects/roles borrowed from unauthenticated identities with the appropriate 401/403 |
| Real PostgreSQL ownership/replay | 1 flow binds quote, parcel, history and idempotency to fixed Customer A despite fake B headers. B cannot consume A's quote or obtain A's completed replay even with fake A headers. Denials leave counts unchanged; A replays identical bytes/Location with the same pending event |

The unknown settings in the negative tests are deliberate hostile inputs, not
supported configuration options. Unsigned-token rejection exercises the closed
handler, not a working JWT verifier. Normal-host tests use TestServer with the
real authentication registration; the separate smoke script exercises real
processes and Gateway forwarding. Application handlers, endpoints, Program,
appsettings, database model/migrations, shared assets and project references
are unchanged. README also corrects its stale statement that booking handlers
were still future work.

Verification uses SDK 10.0.301 and an isolated PostgreSQL 18 cluster. No shared
Neon database or broker credentials were used.

| Check | Observed result |
|---|---|
| Prerequisite restore/build/full test before edits | Passed; 470/470 tests; zero build warnings/errors; clean tree and private fixture cleanup |
| Focused authentication suite | 56/56 passed, zero failed/skipped, 50s |
| Final cached restore, all 11 projects | Passed; all up to date; no package changes; online NuGet audit explicitly disabled |
| Final Release solution build | Passed; zero warnings/errors (1.37s) |
| Final full solution regression | 526/526 passed, zero failed/skipped: Delivery 435 (7m26s), Gateway 68 (2m22s), Identity 12, Merchant 6, Notification 5 |
| Delivery source/test formatting | Passed; no changes required after formatting the scoped files |
| EF consistency | Passed; no pending model changes |
| Actual host/Gateway smoke | Passed: five hosts, 11-project reference/cycle audit, 15 absent-feature probes, Identity readiness 503, protected quote/booking 401 despite fake credentials, collection GET 405, private Delivery 8081; all owned hosts stopped |
| Contract compatibility | SHA-256 matches for StatusChanged schema, delivery.proto and Gateway route fixture against docs d97fff4; shared wire assets unchanged |
| Independent review | No blocking issue found in claims selection, fixture isolation or documentation evidence limits |
| Final workspace/fixture cleanup | Zero private PostgreSQL fixture directories remain; main stays at 32604d4; docs/infra/frontend/mobile are clean at their starting commits |
| Docker | Unavailable: engine pipe absent and Docker config access denied; no container execution success claimed |
| Live Identity/JWT, Neon, broker, hosted CI | Not executed; actual token issuer/JWKS and external execution remain integration dependencies |

Final commands from the backend root:

```powershell
dotnet restore SwiftDrop.sln --source 'C:\Users\adith\.nuget\packages' -p:NuGetAudit=false
dotnet build SwiftDrop.sln --no-restore --configuration Release
$env:SWIFTDROP_TEST_POSTGRES_TEMP_ROOT = 'D:\VS code\swiftdrop'
dotnet test tests/SwiftDrop.Delivery.Tests/SwiftDrop.Delivery.Tests.csproj --no-build --no-restore --configuration Release --filter 'FullyQualifiedName~SwiftDrop.Delivery.Tests.Authentication' --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'
dotnet test SwiftDrop.sln --no-build --no-restore --configuration Release --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'
dotnet format SwiftDrop.sln --no-restore --include src/Services/Delivery/SwiftDrop.Delivery.Api tests/SwiftDrop.Delivery.Tests --verify-no-changes --verbosity minimal
dotnet-ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-foundation.ps1 -Configuration Release
git diff --check
docker info --format '{{.ServerVersion}}'
```

The installed EF executable was used without local-tool restore. The smoke
execution policy is process-scoped. No online vulnerability audit, deployed
JWT verification, broker delivery, live Neon result or hosted workflow success
is claimed.

## Checkpoint

Phase 5's completion gate is met: booking is independently testable, normal
hosts remain closed, and the Identity handoff records integration requirements
and unresolved details. The complete checkpoint passed before the local commit
`fix(delivery): isolate authenticated customer claims`. No push, main merge,
Identity/JWT implementation, broker relay or Phase 6 frontend/mobile work is
included. Stop at this checkpoint.
