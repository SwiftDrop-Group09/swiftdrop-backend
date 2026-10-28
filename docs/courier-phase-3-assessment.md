# Courier Phase 3: guarded parcel status transitions

Scope: Phase 3 only. No proof upload/completion, allocation algorithm, history
endpoint or React/Flutter screens. Stop at this checkpoint; no commit/push/merge.

## Baseline and verified contracts before implementation

Reinspected all repositories. Backend is clean on feat/courier-workflow-persistence
at 4723578 (Phase 2 committed externally); its parent 7f0ff55 contains Phase 1.
Docs d97fff4, infra 075a8b4, frontend c062b11 and mobile b819b42 are clean on main.
No staged/unstaged/untracked changes or AGENTS.md found. CONTRIBUTING applies.
Phase 2 source and TRX evidence verified: 694 passed, zero failed/skipped. No
live remote/teammate activity is inferred from local refs.

Read current state matrix, REST StatusInput/operation/idempotency/error rules,
security ownership, data architecture, StatusChanged schema/catalog and tests
TC-R02/TC-REC-05. Reinspected domain, assignments, persistence, replay/history/
outbox triggers, Booking transaction/codec/validation, Phase 2 claims/read handlers
and API middleware. All three existing migrations/snapshot are retained.

- PUT /api/v1/deliveries/{id}/status (UpdateDeliveryStatus), authenticated Courier
  only. Identity is the existing trusted single authenticated UUID sub; no caller
  courier ID, Admin/Dispatcher impersonation or alternate authentication.
- Input: status, expectedVersion integer >=1; notes (nonblank 1-500 for PickedUp,
  InTransit, OutForDelivery); Failed requires RecipientUnavailable, AddressUnreachable,
  Damaged or Other, with optional private notes. Idempotency-Key is required.
- Legal Courier chain: Assigned -> PickedUp -> InTransit -> OutForDelivery;
  Failed from any of those four active states. No skip/repeat/reverse/cancellation,
  assignment or direct Delivered mutation. Delivered requires Phase 4 proof.
- Only the current Active assignment may authorize a new mutation. Persist parcel
  version/status, assignment pickup/terminal change, appended history, one redacted
  StatusChanged v1 outbox and exact replay response in one database transaction.
- Same key/payload returns its original result without another event; changed
  payload/key expiry and stale version/state return 409. Unauthorized/foreign
  requests get 401/403/404 without private state/version disclosure.

StatusInput includes Failed on this same operation, so Phase 3 must guard its
required reason and terminal assignment atomically rather than expose an unsafe
partial transition. This is shared transition-contract enforcement; no separate
failure API or client flow is added. Phase 5 can verify/extend the failure-reporting
work package against this foundation. The approved photo-OR-signature policy is
unchanged; no success proof endpoint is introduced.

## Transaction, replay and compatibility decisions

Use the existing application transaction-port pattern and Delivery DbContext.
Lock the target parcel and then its assignment in the same transaction; recheck
assignment with a fresh query after acquiring the parcel lock. Keep those locks
through commit, and retain actual EF parcel Version/xmin predicates. The later
dispatcher writer must honor the documented parcel/version locking contract.
No new assignment process, counters, broker or notification writer is introduced.

Reserve the principal/UpdateDeliveryStatus/key scope with the existing unique
index/INSERT ON CONFLICT pattern; fingerprint includes parcel, canonical status,
expectedVersion, private note and failure reason. Replays precede version/state
checks but still require related assignment authorization: reassigned former
Couriers get 404. For a committed Failed operation, its original Courier may
retrieve their own minimal stored result using the retained Failed assignment;
this cannot change state or read new private data. New terminal-state mutations
are rejected. This reconciles terminal assignment release with required safe retry.

The existing JSONB replay constraint/Complete method permits only 201. Extend
IdempotencyRecord with a focused status-result method and add a migration allowing
200 only for UpdateDeliveryStatus, preserving every previously valid 201 envelope,
30-day expiry and immutable retained results. Do not broaden Booking's 201 method.
The stored envelope retains its existing statusCode/body/location layout. The
status response reuses the six common Parcel fields/price snapshot from Booking;
private details/history stay on the authorized GET route and out of replay receipts.

## Exact file-level plan before feature edits

- Domain/CourierTransitionPolicy.cs: the centralized approved Courier submatrix
  and documented failure enum; extend Parcel with guarded versioned advancement.
- Extend BookingHistory and OutboxMessage factories using existing entities,
  private-note/reason columns and the unchanged ten-field StatusChanged v1 body.
- Application/Idempotency/IdempotencyInput.cs: extract existing key validation
  without changing Booking behavior; reuse it for both entry adapters.
- Application/CourierStatus: input/validation/result/transaction ports, canonical
  fingerprint, focused handler and exact response codec.
- Infrastructure/CourierStatus/PostgresCourierStatusStore.cs: transactional row
  locks, assignment recheck, existing replay reservation, tracked atomic save and
  safe persistence/conflict mapping. No direct RabbitMQ publish.
- Domain/IdempotencyRecord.cs and DeliveryEntityConfigurations.cs: compatible
  200 replay extension; new CourierStatusIdempotency migration/designer/snapshot.
  Preserve all existing migration files and triggers.
- Presentation/CourierStatusEndpoints.cs and CourierStatusProblems.cs; minimal DI,
  routing/auth-error/private-cache additions to Program.cs. No Phase 2/Booking
  request/response changes.
- Courier status domain/application/HTTP/PostgreSQL tests for authorization,
  legal/illegal/stale writes, same/different-key races, reassignment race, exact
  replay, private history/event schema, lock waits/cancellation, rollback/fault
  injection, upgrade preserving Booking and real Booking/read regressions.
- Backend README, infrastructure runbook, foundation/smoke denial probes and this
  report. No unrelated repository changes or added runtime package.

Run restore, Release build, targeted/full suites on disposable PostgreSQL, EF
script/model checks and actual host/Gateway smoke. Record blocked Docker, real
JWT, Neon and broker/storage integration separately from passing tests. No
cross-system atomicity or notification publication is claimed.

## Implementation and verification results

28 October 2026. Phase 3 implements the backend portion of FR-R02 and the guards
required by TC-R02 and TC-REC-05. The application checkpoint is complete under
isolated trusted test principals; live Identity acceptance remains a dependency.
This does not establish whole-workflow, proof, broker-consumer or client acceptance.

### Implemented behavior and API

The exact documented PUT route accepts a strict camelCase StatusInput and one
Idempotency-Key. Duplicate/unknown JSON fields, forged authorization fields,
numeric/aliased states, noninteger/missing/nonpositive versions, invalid failure
codes and missing/oversized stage notes return 400. Courier authentication and
role checks precede body processing and database resolution. No new status enum,
assignment lifecycle, dispatcher route, proof requirement or event field is added.

The central domain policy admits only the seven documented Courier transition
pairs. Pickup records the assignment pickup timestamp. Failed closes the assignment,
releases the Active slot and removes it from the existing manifest; it cannot
reactivate or reassign the parcel. Notes/reasons stay in append-only private history.
Direct Delivered, skips, reverse transitions and repeats return invalid_transition
409; stale expected versions return stale_version 409 with authorized currentVersion.
No private state/version is returned for foreign/missing parcels (404), invalid
identity (401) or wrong role (403). Storage failures return safe 503 responses.

Status 200 returns the established six-field Parcel receipt: id, trackingNumber,
status, version, priceSnapshot and createdAt. The separately authorized GET keeps
the detailed projection. The PUT response omits notes, reasons, Courier/Customer IDs,
assignment history and proof/storage metadata. Its stored JSON body is a string
inside the existing replay envelope, preserving exact response bytes. Correlation
IDs describe the current HTTP request; a replay does not rewrite the original event.
Successful status replies have no new Location header requirement; the retained
envelope's canonical parcel location is used for replay integrity checks.

One accepted write increments Version exactly once and atomically saves assignment,
history, StatusChanged v1 outbox and completed replay record. There is no intermediate
publish. The event retains the existing ten-field schema, nullable merchantId and
stable event/parcel/version identity, with no private note, failure reason, contact,
Courier ID, proof reference or storage URL. Existing Customer creation uses the
same original payload shape and 201 replay behavior.

### Files changed

All 31 changed files (18 new, 13 modified) are confined to swiftdrop-backend.
Paths below are relative to
`src/Services/Delivery/SwiftDrop.Delivery.Api/` unless stated otherwise.

| Files | Change |
|---|---|
| `Domain/CourierTransitionPolicy.cs` (new) | Central transition matrix, failure codes, metadata guards and typed domain conflict |
| `Domain/Parcel.cs` | Guarded expected-version advancement; original creation unchanged |
| `Domain/BookingHistory.cs` | Append-only private Courier history factory |
| `Domain/OutboxMessage.cs` | Courier transition factory reusing the original event construction |
| `Domain/IdempotencyRecord.cs` | Focused 200 status completion; original 201 method retained |
| `Application/CourierStatus/CourierStatusContracts.cs` (new) | Input/result DTOs and transaction port |
| `Application/CourierStatus/CourierStatusValidator.cs` (new) | Strict validation and canonical operation fingerprint |
| `Application/CourierStatus/CourierStatusHandler.cs` (new) | Trusted actor, assignment, version, replay and atomic write orchestration |
| `Application/CourierStatus/StatusResponseCodec.cs` (new) | Minimal receipt and strict exact-result replay validation |
| `Application/Idempotency/IdempotencyInput.cs` (new), `Application/Bookings/BookingInputValidator.cs` | Reuse the existing key rule without changing Booking validation |
| `Infrastructure/CourierStatus/PostgresCourierStatusStore.cs` (new) | Parcel/assignment row locks, ownership recheck, existing reservation pattern and atomic persistence |
| `Infrastructure/Persistence/DeliveryEntityConfigurations.cs` | Compatible operation-scoped 200 replay check |
| `Infrastructure/Persistence/Migrations/20261028170439_CourierStatusIdempotency.cs` and `.Designer.cs` (new), `DeliveryDbContextModelSnapshot.cs` | One new constraint-only migration and matching model |
| `Presentation/CourierStatusEndpoints.cs`, `Presentation/CourierStatusProblems.cs` (new) | Exact route, strict HTTP adapter and safe errors |
| `Program.cs` | Scoped DI, endpoint registration and status-specific authentication/private-cache response metadata |
| `Infrastructure/README.md` | Status transaction, replay, rollout and downgrade runbook |

Other backend-relative paths:

| Files | Change |
|---|---|
| `tests/SwiftDrop.Delivery.Tests/Courier/CourierStatusDomainTests.cs` (new) | Full transition matrix, metadata, fingerprint and event privacy |
| `tests/SwiftDrop.Delivery.Tests/Courier/CourierStatusAuthenticationTests.cs` (new) | Direct-host security, roles/identity and strict JSON/input rejection |
| `tests/SwiftDrop.Delivery.Tests/Courier/CourierStatusApiTests.cs` (new) | Real PostgreSQL HTTP transitions, assignment ownership, replay and races |
| `tests/SwiftDrop.Delivery.Tests/Courier/CourierStatusReliabilityTests.cs` (new) | Fault rollback, lock waits, cancellation and lost acknowledgement/response |
| `tests/SwiftDrop.Delivery.Tests/Courier/CourierStatusMigrationTests.cs` (new) | Real Customer Booking before/after upgrade and protected status receipts |
| `tests/SwiftDrop.Delivery.Tests/Courier/StatusTestScenario.cs` (new) | Synthetic owned fixtures and test-only dispatcher transaction using the documented lock/version contract |
| `tests/SwiftDrop.Delivery.Tests/Bookings/Reliability/BookingOutboxContractTests.cs` | Expose the existing checked-in-schema assertion internally for reuse; original tests unchanged |
| `tests/SwiftDrop.Delivery.Tests/FoundationTests.cs` | Additional fail-closed PUT authentication probe |
| `scripts/verify-foundation.ps1` | Real Gateway PUT denial/private-cache smoke probe, retaining all previous probes |
| `README.md`, `docs/courier-phase-3-assessment.md` (new report) | Current behavior, evidence, mismatches and integration handoff |

No runtime/test package or additional service was introduced. Identity, Merchant,
Gateway routing, Worker, Customer Booking transaction/response, read handlers,
tracking, quote pricing, original migrations and retention triggers are preserved.
Program.cs, shared domain factories, EF configuration/snapshot and the replay
constraint are the deliberate shared Delivery review points for teammates.

### Database rollout

The new migration changes only ck_idempotency_completion, accepting the union of
all previously valid 201 receipts and 200 receipts whose operation is exactly
UpdateDeliveryStatus. No table/column/index or event schema changes are needed.
It does not rewrite existing receipts. Existing append-only history, immutable
replay/outbox, unique Active assignment and quote/snapshot triggers remain intact.
Generate/review the SQL and apply through the established Delivery-only migration
task before enabling the endpoint. Normal startup still performs no migration.

Upgrade tests book a real Customer parcel on the previous schema, migrate, update
its status and replay the exact original 201 receipt. A nonstatus 200 envelope and
receipt alteration are rejected by PostgreSQL. Downgrade to the previous 201-only
schema fails with check violation when retained 200 receipts exist; the transaction
rolls back and keeps the receipt. Do not delete retained records to force rollback.
Rolling back application code while retaining the compatible schema, or a reviewed
forward migration, is the appropriate deployment path. No shared Neon DDL was run.

### Tests and actual results

Used the pinned temporary SDK 10.0.301, dotnet-ef 10.0.9 and installed PostgreSQL 18.
Test commands use the existing fixture's generated private loopback clusters, not
shared Neon. Cleared SWIFTDROP_TEST_DELIVERY_DATABASE in the test process and set
SWIFTDROP_TEST_POSTGRES_TEMP_ROOT to the test-owned temporary parent. The restore
uses a process-local public NuGet configuration because the sandbox cannot read
the user configuration; no package source or global SDK settings were changed.

```powershell
dotnet restore SwiftDrop.sln --configfile "$env:TEMP/swiftdrop-courier-nuget.config" --disable-parallel
dotnet build SwiftDrop.sln --no-restore --configuration Release
dotnet test tests/SwiftDrop.Delivery.Tests --no-build --configuration Release --filter 'FullyQualifiedName~CourierStatus' --logger 'trx;LogFilePrefix=courier-phase3-targeted' --results-directory artifacts/courier-phase3-targeted
dotnet test SwiftDrop.sln --no-build --configuration Release --logger 'trx;LogFilePrefix=courier-phase3-final' --results-directory artifacts/courier-phase3-final
dotnet ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
dotnet ef migrations script --idempotent --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --output artifacts/courier-phase3-migrations.sql
./scripts/verify-foundation.ps1 -Configuration Release
```

- Restore passed. Release build passed with zero warnings and zero errors.
- The first new-test build caught EF1002 for dynamically composed fixture-only
  fault-injection DDL. Corrected it to explicit commands with fixed/validated table
  names and generated UUID identifiers; no analyzer suppression or caller SQL input.
  The subsequent build passed; no failing runtime test is hidden by that correction.
- Targeted Phase 3 suite: **139 passed, 0 failed, 0 skipped** (1m26s).
- Final full solution: **834 passed, 0 failed, 0 skipped**: 743 Delivery, 68 Gateway,
  12 Identity, 6 Merchant and 5 Notification. Delivery ran in 4m13s. This includes
  all 694 previous-phase cases and 140 added cases (139 targeted plus the foundation
  PUT authentication probe). Real Booking, read projections, quote/fee, immutable
  price snapshot, tracking, idempotency, history/outbox and Merchant compatibility
  regressions all passed.
- EF model check passed: no pending changes. Idempotent migration script generated
  successfully in ignored artifacts; no shared database connection is needed.
- Actual host/Gateway smoke passed: 12-project dependency/cycle checks, all five
  hosts' health/startup, 15 missing/internal/readiness probes, original Booking/
  quote and Phase 2 read security probes, and the new status PUT 401/private/no-store
  probe. The script stopped every host it started.
- Docker verification remains blocked: docker version found no docker_engine pipe;
  the sandbox also denied reading the user Docker configuration. Container image/
  stack tests were not executed or claimed.
- Final whitespace and protected-source diff checks passed. Backend HEAD/branch
  remained unchanged, with no staged files. Identity, Merchant, Gateway, Worker,
  Contracts, existing Booking transaction, read/auth adapters and all prior
  migration files have no changes. All other repositories remain clean at their
  verified initial commits. The generated SQL contains the new migration and
  operation-scoped 200 check.

New tests include all 64 status pairs, all four failure codes and active failure
sources, wrong/previous Courier and role denials, strict body/identity validation,
exact replay/changed fingerprint/30-day expiry, same-key and different-key races,
reassignment versus pickup, PostgreSQL lock timeout/cancellation, and injected
parcel/assignment/history/outbox/receipt/deferred-COMMIT failures. They verify no
partial state, reservation or event on failure and safe retry after recovery.
Lost commit acknowledgement and lost HTTP result are resolved through exact
stored replay with no additional event. The dispatcher race fixture is test-only;
it does not claim integration with an implemented dispatcher API.

Console outputs and TRX are under ignored artifacts/courier-phase3-*; they are
local execution evidence, not committed CI or coverage/performance claims.

### Security, concurrency and integration dependencies

Production continues to register UnconfiguredIdentityHandler and rejects all
unverified bearer strings and identity headers. Only the established isolated test
host supplies trusted principals. No Identity DB, password or token issuer was
added. There is no production development-auth switch. Every status response is
private/no-store and safe ProblemDetails never exposes SQL, private note/reason,
another actor's state/version, proof references or credentials.

READ COMMITTED parcel-first then assignment row locks remain held through commit;
the assignment is re-read after a parcel lock wait. Actual EF Version/xmin predicates
also remain active. Only one new write from a given expected version can commit.
Current ownership is rechecked before reservation and mutation; a reassigned actor
gets no receipt or currentVersion. Terminal Failed replay is limited to the same
actor's retained result and does not grant private GET access or a new transition.
An unknown commit result requires the original request/key; a new key is not a
safe substitute. A stale client must reload before intentionally choosing a new
action. No automatic retries under a different identity or key occur.

External dependencies remain:

- Identity issuer/JWKS/signature/audience/expiry validation and live Courier role
  provisioning. Real JWT acceptance/revocation-bound tests require that integration.
- Delivery-owned operational Courier/zone provisioning and the dispatcher writer
  following the same parcel/version lock order and atomic release/replacement.
  No assignment algorithm or capacity counter is invented here.
- Reviewed migration deployment and Neon role/TLS/grant verification. This run
  establishes disposable PostgreSQL behavior, not shared deployment success.
- The existing outbox relay and downstream inbox/notifications. Pending rows are
  durable; broker publication, consumer effects and end-to-end timing are untested.
- Phase 4 private object storage/proof completion; approved photo OR signature
  policy remains unchanged. React and Flutter Courier screens stay Phase 7 work.
- An available Docker engine for container build/start verification.

### Documentation alignment and next checkpoint

The current [REST operation/StatusInput](../../swiftdrop-docs/api/rest-api.md),
[state matrix](../../swiftdrop-docs/architecture/parcel-state-machine.md),
[authorization](../../swiftdrop-docs/security/authorization.md) and
[StatusChanged schema](../../swiftdrop-docs/api/contracts/status-changed-v1.schema.json)
agree on the route, transitions, private notes, failure codes, version and event
requirements. Their Proposed approval markers remain unchanged; implementing the
requested phase does not mark any ADR Accepted. FR-R02's final proof step remains
Phase 4, and FR-R04's failure-client/checkpoint package remains Phase 5.

REST specifies Parcel projection groups rather than a separate status-receipt
schema. This implementation uses the existing six common Booking fields and leaves
details on the authorized GET, as recorded above. The docs do not explicitly describe
replaying a command after terminal assignment release; the minimal same-actor Failed
receipt rule reconciles their exact-replay guarantee with current-owner privacy.
It does not authorize a historical actor to advance status or access details.

The prior 201-only replay check could not store the documented status operation's
200 result; the compatible new migration resolves this implementation mismatch.
The original assignment PDF permits note or proof for final completion; the user
explicitly approved the stronger at-least-one-photo-or-signature policy earlier.
No proof rule is changed or implemented in this phase. Deployment docs remain stale:
environments.md's connection-string paragraph and local-setup.md's current-feature
section still describe Delivery's DB adapter as future. The docs repository is
left unchanged; these mismatches are reported instead of silently concealing them.

Next recommended phase: **Phase 4 — private delivery proof and completion**, after
an explicit phase instruction and verification of the current upload/storage contract.
Stop at Phase 3. No Phase 4 handler, upload, client flow, commit, push or merge has
been performed. The backend HEAD remains 4723578; the other four repository HEADs
and working trees remain unchanged. Remote activity was not fetched or inferred.
