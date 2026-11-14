# Courier Phase 2: assigned manifest and parcel details

28 October 2026. Phase 2 only; no status mutations, proof upload, history endpoint
or client screens. No commit, push, merge or shared database migration authorized.

**Phase 2 implementation checkpoint met:** both verified read routes work under
strict resource authorization in the isolated PostgreSQL/API tests. Release build,
EF model check and real host/Gateway smoke checks pass. Final solution suite:
**694 passed, 0 failed, 0 skipped**, including 68 new Phase 2 cases. Production
continues to fail closed pending the external Identity JWT integration. Phase 3
has not started.

## Baseline verified before implementation

Backend is on feat/courier-workflow-persistence at 884f576, with the 21 known,
uncommitted Phase 1 files preserved. No staged changes or additional developer
changes were found. Docs d97fff4, infra 075a8b4, frontend c062b11 and mobile
b819b42 remain clean on main. No AGENTS.md found; backend CONTRIBUTING applies.
Remote refs were not fetched, so this does not assert live teammate activity.

During implementation the same branch advanced externally to `7f0ff55`
(`feat: Implement Assignments Persistence and Domain Logic`). Inspected the
commit: it contains exactly the 21 earlier Phase 1 source/test/doc files; no
Phase 2 files or overlapping edits were committed. This agent did not commit.
The new baseline is preserved; Phase 1 persistence/migrations, domain, Booking
handlers, original Customer claims adapter, Identity and Merchant sources remain
unchanged relative to it. There was no conflicting live edit to resolve.

Read current REST/reference/routes, SRS FR-R01, TC-R01/security cases, security
authentication/authorization, data/state-machine and environment/timezone rules.
Reinspected Program, claims adapter, booking contracts/materialization, persistence,
Phase 1 domain/migrations and test fixtures. Phase 1 TRX evidence independently
totals 626 passed, zero failed/skipped. Its operational profiles, current-Active
assignment invariant, retained history, metadata and concurrency tokens exist.
The user's approved lifecycle and photo-OR-signature policy remain in force.

## Verified contracts and implementation decisions

- GET /api/v1/couriers/me/assignments (CourierAssignments): Courier self from one
  authenticated identity with one nonempty UUID sub and Courier role. Never
  authorize from courierId/headers. Query date YYYY-MM-DD defaults to today's
  configured Operations:TimeZone (existing infra uses Asia/Colombo), includeOverdue
  defaults true, page defaults 1, pageSize defaults 20/max 100. Return items,
  totalCount, page, pageSize. Include current Active assignments for the selected
  service day and, when requested, earlier still-active work; exclude future work
  and terminal/unassigned parcels. isOverdue compares serviceDate with selected day.
- GET /api/v1/parcels/{id} (GetParcel): shared route, not a new Courier alias.
  Current assigned Courier, owning Customer, or Dispatcher/Admin may read. A
  Customer cannot use the Courier manifest, but can read their own parcel through
  this documented shared contract. Foreign/missing/malformed IDs return safe 404;
  missing/malformed identity 401, unsupported role 403. No writes are enabled.
- Use the existing minimal Parcel fields and price-snapshot shape. Reuse the
  existing structured address/recipient contract. Manifest adds package details,
  pickup/delivery/recipient and serviceDate/isOverdue. Detail adds current assignment
  timestamps/status and actual private history. Exclude actor/customer/merchant
  identifiers, previous Courier identities, raw storage refs, proof data, outbox,
  quote/replay metadata and credentials. Do not serialize persistence entities.
- Stable newest-first parcel createdAt/id paging; history timestamp/id ascending.
  PostgreSQL repeatable-read snapshots keep count/page and authorization/detail/
  history consistent without writing or locking unrelated bookings. No status or
  version changes. Read authorization occurs in the resource query itself.
- Validate queries after authentication/role checks, including duplicate/unknown
  parameters and offset overflow. Errors use safe ProblemDetails/correlationId;
  private responses use Cache-Control: private, no-store. Cancellation propagates.

REST documents projection groups but not complete concrete JSON shapes. These
source DTOs make those shapes explicit using existing field names; no new operation
or business rule is inferred. Detailed roles follow REST/security, not a Courier-only
restriction on GetParcel. Architecture ADRs remain Proposed. Actual production JWT
verification is absent: retain UnconfiguredIdentityHandler and test-only principals;
never enable unsigned bearer/header authentication to make positive tests work.

## File-level plan before feature edits

- Application/ICurrentDeliveryUser.cs and Infrastructure/Authentication/
  ClaimsCurrentDeliveryUser.cs: read identity/roles from the trusted principal;
  leave existing Customer identity adapter/booking semantics unchanged.
- Application/DeliveryReads: contracts/store port, query validation, Operations
  service-day clock, focused authorization/query handler.
- Infrastructure/DeliveryReads/PostgresDeliveryReadStore.cs: owner/current-assignment
  predicates, explicit DTO projections, bounded paging and private history reads.
- Presentation/DeliveryReadEndpoints.cs and DeliveryReadProblems.cs: two verified
  GET routes and consistent errors/private headers. Program.cs only adds DI/routes
  and routes authentication errors to the read-specific formatter via metadata.
- appsettings.json: explicit documented Operations:TimeZone=Asia/Colombo.
- Tests: trusted claims attack cases; application identity/validation/dependency/
  timezone cases; real PostgreSQL HTTP own/foreign/previous assignment, date and
  paging, customer/operations shared-route access, proof/privacy and no-write checks.
  Add denial probes to existing foundation/smoke checks where relevant.
- Update backend README/runbook and this report with exact changes/results.

No new tables, migrations, packages, login system, provisioning/assignment process,
states, proof requirements or events. Preserve all prior uncommitted Phase 1 work.
Use the pinned temporary SDK and existing disposable PostgreSQL fixtures. Run
targeted tests, Release solution build/full tests, EF pending-model check and host
smoke checks; report blocked Docker/live JWT/storage/broker checks separately.

## Implemented functionality and actual changed files

Implemented both verified GET operations, query validation, current-principal
scope, explicit safe DTOs, service-day defaults, consistent PostgreSQL projections,
private/no-store headers and read-specific ProblemDetails. Default date is server
configured, not client/device derived. Current Courier access is enforced through
Active assignment and active parcel predicates. The shared detail route separately
supports owning Customers and Dispatcher/Admin, following its verified contract.

All Phase 2 changes are in **swiftdrop-backend**:

- Application: ICurrentDeliveryUser.cs; DeliveryReads/DeliveryReadContracts.cs,
  DeliveryReadHandler.cs, OperationsClock.cs.
- Infrastructure: Authentication/ClaimsCurrentDeliveryUser.cs;
  DeliveryReads/PostgresDeliveryReadStore.cs.
- Presentation: DeliveryReadEndpoints.cs, DeliveryReadProblems.cs.
- Composition/configuration: Delivery Program.cs and appsettings.json.
- Tests: Courier/DeliveryReadAuthenticationTests.cs,
  Courier/DeliveryReadApplicationTests.cs, Courier/CourierReadsApiTests.cs;
  FoundationTests.cs; scripts/verify-foundation.ps1 adds actual Gateway GET denial
  and private-cache probes without removing prior Booking checks.
- Documentation: backend README.md, Delivery Infrastructure/README.md, this report.

Database changes: **none**. No migration, model, entity, history, version, outbox,
tracking number, quote, price snapshot, idempotency or existing Booking API writes
changed. API additions only: CourierAssignments and GetParcel, named exactly as
the route inventory. No list-parcels, history, status, assignment, proof, SSE or
GraphQL endpoint was enabled.

## Security and concurrency evidence

Claims tests cover custom role claim types, roles claims, missing/invalid/duplicate
subjects, untrusted secondary identities and multiple authenticated identities.
HTTP tests cover anonymous and production unverified-bearer/header requests,
Customer/Merchant/operations manifest denial, scope injection and validation
without a configured DB. Authorized storage/config failures return safe 503.

PostgreSQL HTTP scenarios cover own versus foreign/previous/unassigned parcels,
selected-day/overdue/future filtering, UTC-to-business-day boundary, stable paging,
empty pages, documented Customer/operations access, Merchant-origin ownership,
minimal response fields and absence of proof refs/actor IDs. Before/after state
comparisons verify reads do not alter parcel status/version, assignments or row
counts for quotes/history/outbox/replay/proof/snapshots. A real Customer quote and
confirmation test follows Location and compares every original booking response
field against the private detail read.

A database-command interceptor commits reassignment on another connection after
the manifest count query. The page remains consistent with its authorized
repeatable-read snapshot; subsequent requests deny the old Courier and allow
the new one. This does not claim revocation of bytes from an already-authorized
in-flight snapshot or implement write-time concurrency (Phase 3).

The first targeted run had 55 passes and 7 failures caused by EF being unable to
order a positional intermediate-record projection. Corrected it to explicit
member bindings so sorting/paging stays in SQL; no client-side paging fallback.
The rerun passed all 62 then-present targeted cases. Additional anonymous, query
scope, real booking/read and foundation cases are included in the final suite.

## Verification commands and results

Using the existing pinned temporary SDK 10.0.301 and dotnet-ef 10.0.9; process-local
DOTNET_ROOT/PATH/DOTNET_CLI_HOME and the existing temporary NuGet configuration.
PostgreSQL uses fixture-owned disposable local clusters; the external test DB
fallback is unset. No live Neon connection or shared database writes are used.

```powershell
dotnet restore SwiftDrop.sln --configfile "$env:TEMP/swiftdrop-courier-nuget.config" --disable-parallel
dotnet build SwiftDrop.sln --no-restore --configuration Release
dotnet test tests/SwiftDrop.Delivery.Tests --no-restore --configuration Release --filter 'FullyQualifiedName~DeliveryReadAuthenticationTests|FullyQualifiedName~DeliveryReadApplicationTests|FullyQualifiedName~CourierReadsApiTests' --logger 'trx;LogFilePrefix=courier-phase2-fixed' --results-directory artifacts/courier-phase2-fixed
dotnet test SwiftDrop.sln --no-build --configuration Release --logger 'trx;LogFilePrefix=courier-phase2-final' --results-directory artifacts/courier-phase2-final
dotnet ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
./scripts/verify-foundation.ps1 -Configuration Release
git diff --check
```

- Restore: passed, all projects up to date; no package changes.
- Final Release build: passed, zero warnings/errors.
- Corrected targeted suite: 62 passed, zero failures/skips.
- EF model check: passed, no pending changes.
- Final complete solution: **694 passed, 0 failed, 0 skipped**; 603 Delivery,
  68 Gateway, 12 Identity, 6 Merchant and 5 Notification tests. This includes all
  626 Phase 1/baseline cases and 68 new Phase 2 cases (66 read-specific cases and
  two foundation authentication probes). Delivery ran in 3m34s.
- Actual host/Gateway smoke: passed project-reference/cycle audit, all five host
  startup/health checks, 15 missing/internal/readiness probes, original quote/
  booking denial checks and the new manifest/detail 401/private/no-store probes.
  The script stopped every process it started.
- Diff whitespace and protected-source checks: passed. Phase 1 domain/persistence/
  migrations, original Customer claims/Booking adapters, Identity, Merchant,
  outbox/history rules and event contracts have no Phase 2 edits. Other four
  repositories remain clean at their initial commits.
- Docker image/start verification: blocked/unexecuted. `docker version` found
  client 29.5.2 but the engine pipe was absent; the sandbox also could not read
  the user's Docker configuration. No successful container test is claimed.

Final TRX reports are in ignored `artifacts/courier-phase2-final/`, and console
output in `artifacts/courier-phase2-final-output.txt`. Earlier failing/corrected
targeted artifacts are retained separately. No coverage percentage, performance
target or real JWT acceptance is inferred from these results. The full regression
run includes the existing Booking validation, quote/fee, idempotency/race/replay,
price snapshot, tracking-generation, history/outbox and Merchant compatibility
tests; all pass alongside the new real-booking Location read scenario.

## Documentation alignment and remaining dependencies

FR-R01/Phase 2 and TC-R01's backend scenarios are implemented. Existing REST only
specifies projection groups; this report and source DTOs define their concrete
field layout using existing address/contact and price-snapshot shapes. No extra
route/state/schema was invented. GetParcel's shared owner/operations permissions
are intentional; Customer denial applies to the Courier manifest, not its own
documented parcel detail. All still-Proposed ADRs retain that status. Previous
deployment docs saying Delivery has no database adapters remain stale as recorded
in Phase 1; this task does not modify the docs repository to conceal mismatches.

Production Identity token issuance/JWKS verification, verified operational Courier
provisioning and the dispatcher assignment writer remain external dependencies.
Proof storage/broker integration is not required by these read handlers and is
not claimed as tested. Docker/shared deployment and real JWT tests require their
actual environment. React/Flutter Courier screens remain Phase 7, not omitted
from the overall scope. No coverage percentage or full workflow acceptance is
claimed. Next recommended phase: Phase 3 guarded, versioned status transitions,
only after an explicit instruction. Stop at the Phase 2 checkpoint.

No Phase 2 changes have been staged, committed, pushed or merged by this agent.
The external Phase 1 commit 7f0ff55 is preserved. Program.cs and the shared
GetParcel route are integration points for future Delivery work: reuse these
handlers/DTOs instead of registering a competing detail endpoint. Real Courier
JWTs, migrated service-owned storage and actual dispatcher-created assignments
are prerequisites for a non-test operational demonstration. React/Flutter
clients, private proof/broker integration and full cross-service acceptance
remain explicitly unexecuted later-phase work.
