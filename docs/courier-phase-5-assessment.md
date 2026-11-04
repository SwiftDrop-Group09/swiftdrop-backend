# Courier Phase 5: failed delivery reporting

29 October 2026. Phase 5 only: FR-R04 and TC-R04. Stop after this checkpoint;
no Courier history endpoint, clients, assignment algorithm, commit, push or merge.
The backend Phase 5 checkpoint is complete under isolated trusted principals and
disposable PostgreSQL; live integration acceptance remains as listed below.

## Inspection and assessment before edits

Backend is feat/courier-workflow-persistence at d663387. Phase 4's existing
uncommitted work is preserved; its actual final TRX counters are 926 passed,
zero failed/skipped (835 Delivery, 68 Gateway, 12 Identity, 6 Merchant and
5 Notification). Its status/proof source, migration, retention, outbox and
test evidence were inspected. The starting hashes of all dirty files and the
shared reliability test helper were recorded in ignored
artifacts/courier-phase5-starting-files.json. No later developer commit or new
overlapping work was found. Docs/main d97fff4, infra/main 075a8b4,
frontend/main c062b11 and mobile/main b819b42 remain clean. No AGENTS.md applies;
backend CONTRIBUTING applies. Remote activity has not been fetched or inferred.

Read the original master prompt Phase 5, SRS FR-R04, use case/traceability,
REST StatusInput and UpdateDeliveryStatus, full state matrix, authorization,
data/history/idempotency constraints, StatusChanged schema/catalog and TC-R04.
The architecture is still Proposed; no ADR is marked Accepted. Previously
approved assignment lifecycle and photo-OR-signature completion policy remain.

Verified failure contract:

- PUT /api/v1/deliveries/{id}/status, existing UpdateDeliveryStatus operation;
  status Failed, expectedVersion >=1, required Idempotency-Key and failureReason.
- Allowed reasons: RecipientUnavailable, AddressUnreachable, Damaged, Other.
  Notes are optional/private; Other does not require an invented explanatory note.
- Source state Assigned/PickedUp/InTransit/OutForDelivery only. Current Courier
  identity/role/Active assignment must authorize the mutation at commit.
- Increment parcel version, close assignment as Failed, retain reason/note in
  append-only private history and append one unchanged StatusChanged v1 outbox
  plus exact replay result in the same transaction. No automatic reassignment,
  new failure event, proof prerequisite, retry/redelivery state or separate API.
- Failed is terminal. Exact original-key replay is safe after assignment closure;
  a new action cannot resume or complete this parcel. Missing/invalid reason 400,
  wrong role 403, foreign/former assignment 404, stale/state/key conflict 409.

The existing Phase 3/4 handler, centralized policy, validator, locked PostgreSQL
store and domain/history/outbox factories already implement this contract.
All four reasons and all four active source states already have passing tests.
There is no justified production feature rewrite or schema addition for Phase 5.
Complete its remaining acceptance evidence against the real HTTP/PostgreSQL path.

## File-level plan

- New tests/SwiftDrop.Delivery.Tests/Courier/CourierFailureApiTests.cs: failure-
  specific invalid reason/no-effects, stale and foreign/former authorization,
  private optional notes/history, terminal-state/proof denial and reason races.
- New tests/SwiftDrop.Delivery.Tests/Courier/CourierFailureReliabilityTests.cs:
  failure-specific rollback at parcel/assignment/history/outbox/replay/commit,
  original-key recovery and lost response after terminal assignment release.
- CourierStatusReliabilityTests.cs: expose only its existing fixture-owned fault
  and response-loss helpers internally for reuse, without changing their behavior.
- scripts/verify-foundation.ps1: exercise a valid Failed request through the real
  Gateway's fail-closed authentication path in addition to the existing probes.
- New docs/courier-failure-reporting.md, this report, README.md and Infrastructure/
  README.md: verified wire/error/terminal/retry/ownership handoff and actual evidence.

No production C# file, dependency, migration, event schema, authentication adapter,
Booking flow or Phase 4 proof implementation needs to change. Run restore/Release
build, focused failure tests, full solution, EF model/script checks and actual
host/Gateway smoke. Preserve existing source hashes outside the listed docs/test/
smoke changes and report live Identity/broker/Docker/storage limits separately.

## Actual results

An external commit 934c3c6 appeared during this run. It contains Phase 4 plus the
initial Phase 5 assessment and the two test-helper visibility changes. This agent
did not create or stage that commit. Its contents were inspected and preserved.
Hash comparison confirms every starting file outside the four planned docs/test/
smoke edits remains byte-for-byte unchanged. Backend stays on the same feature
branch; no unexpected production changes have been introduced by this phase.

## Implemented behavior and acceptance coverage

Failed delivery is available through the existing status operation; Phase 5
completes its acceptance and handoff work rather than duplicating working domain
logic. The handler already validates the trusted Courier, required reason and
current assignment/version, closes the assignment, and saves failure history,
parcel/version, StatusChanged v1 and exact replay atomically. It neither depends
on proof storage nor publishes directly to a broker. No production defect was
found that justified a behavior change.

| Phase 5 requirement | Actual implementation / verification |
|---|---|
| Valid failure and reason codes | Existing CourierStatusApiTests cover all four codes and four allowed source states; new cases cover nullable/500-character private notes and Other without notes |
| Required valid reason | Six malformed/missing reasons return 400 without assignment/version/history/outbox/key changes; a corrected same-key request succeeds |
| Current Courier only | Valid failure rejected for foreign/former Courier and Customer/Merchant/Dispatcher/Admin; replacement Courier succeeds, with prior assignment retained |
| Expected version | Failure-specific stale request leaves Active assignment and all records intact; no stale reservation is consumed |
| Terminal lifecycle | All eight attempted target states from Failed are rejected without effects; proof cannot resume Failed; failure cannot overwrite a committed Delivered parcel |
| Privacy and history | Actor, previous state, UTC timestamp, reason and optional note persist privately; authorized owning Customer can read, foreign Customer/closed Courier cannot read details |
| Canonical outbox | Exactly one schema-valid redacted StatusChanged v1 with correct previous state/version and merchant provenance; no reason/note in event or minimal receipt |
| Atomic rollback | Real PostgreSQL injected faults at parcel, assignment, history, outbox, replay and deferred commit restore the complete graph; original key succeeds after recovery |
| Concurrency | Identical concurrent failure keys replay one result; changed reason/note under same key conflicts; different keys at the same version commit only one winning reason/event |
| Unknown HTTP outcome | Dropped committed Failed response is replayed from another host after assignment closure, without another mutation/event |
| No invented recovery | No automatic assignment, redelivery state, proof prerequisite, extra reason or failure event is introduced |

Existing tests in the full regression also exercise authorization/forged claims,
strict JSON, all 64 status pairs, lock timeout/cancellation, commit acknowledgement
loss, 30-day replay expiry and immutable history/outbox/proof/replay, plus all
Customer Booking and Merchant compatibility behavior. These are inherited passing
capabilities, not new Phase 5 APIs.

## Files changed

Eight backend files form the Phase 5 delta relative to the starting state. Two
(initial assessment/helper visibility) were captured by the external commit noted
above. No other repository is edited.

| File | Phase 5 change |
|---|---|
| tests/SwiftDrop.Delivery.Tests/Courier/CourierFailureApiTests.cs (new) | 25 real HTTP/PostgreSQL cases for validation, authorization, version, terminal states, private history and provenance |
| tests/SwiftDrop.Delivery.Tests/Courier/CourierFailureReliabilityTests.cs (new) | 10 rollback, competing-reason and lost-response cases |
| tests/SwiftDrop.Delivery.Tests/Courier/CourierStatusReliabilityTests.cs | Only LoseResponse and WriteFault visibility changed from private to internal to reuse the existing owned fixtures |
| scripts/verify-foundation.ps1 | Existing status authentication probe now checks both valid pickup and Failed bodies, retaining every other smoke probe |
| docs/courier-failure-reporting.md (new) | Exact request, errors, terminal/retry/ownership/privacy and integration handoff |
| docs/courier-phase-5-assessment.md (new) | Before-edit assessment, contracts, file plan, actual evidence and dependencies |
| README.md | Failure checkpoint/runbook links and correction of the stale statement that Phase 4 proof enforcement remains future |
| src/Services/Delivery/SwiftDrop.Delivery.Api/Infrastructure/README.md | Failure transaction/terminal/receipt handoff and evidence links |

Database/API changes: **none**. No new route, DTO, state, failure code, assignment
rule, proof policy, column, migration, dependency or event schema is needed. No
shared database migration is applied. Existing deployment still needs the already
introduced Phase 1-4 migrations; normal startup continues to perform no DDL.

## Tests and actual execution

Pinned SDK 10.0.301 / EF tooling 10.0.9; PostgreSQL 18 fixture-owned private cluster
on random loopback ports. SWIFTDROP_TEST_DELIVERY_DATABASE is explicitly cleared
in test processes; SWIFTDROP_TEST_POSTGRES_TEMP_ROOT uses the owned temporary
test parent. No shared Neon, live bucket or broker is mutated. NuGet restore uses
the existing process-local public configuration; no dependency/global setting changes.

```powershell
dotnet restore SwiftDrop.sln --configfile "$env:TEMP/swiftdrop-courier-nuget.config" --disable-parallel
dotnet build SwiftDrop.sln --no-restore --configuration Release
dotnet test tests/SwiftDrop.Delivery.Tests --no-build --configuration Release --filter 'FullyQualifiedName~CourierFailure' --logger 'trx;LogFilePrefix=courier-phase5-targeted' --results-directory artifacts/courier-phase5-targeted
dotnet test SwiftDrop.sln --no-build --configuration Release --logger 'trx;LogFilePrefix=courier-phase5-final' --results-directory artifacts/courier-phase5-final
dotnet ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
dotnet ef migrations script --idempotent --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --output artifacts/courier-phase5-migrations.sql
./scripts/verify-foundation.ps1 -Configuration Release
```

- Restore passed; Release build passed with zero warnings and zero errors.
- Focused Phase 5 suite: **35 passed, 0 failed, 0 skipped**, duration 1m19s.
- Full solution regression: **961 passed, 0 failed, 0 skipped**: 870 Delivery
  (5m44s), 68 Gateway, 12 Identity, 6 Merchant and 5 Notification. All 926 previous-
  phase cases and the 35 new failure cases passed. No runtime test failed or was
  suppressed during this phase.
- EF model check passed with no pending changes; idempotent migration script
  generated successfully. There is no Phase 5 migration or new model difference.
- Actual host/Gateway smoke passed: 12-project graph/reference/cycle checks, all
  five hosts' startup/health, 15 missing/internal/readiness probes, quote/Booking,
  manifest/detail, pickup, new failure and proof authentication/private-cache
  probes and private Delivery listener. All processes started by the script stopped.
- Starting-file hash audit passed: only README, Infrastructure/README, smoke script
  and the test helper visibility differ among the pre-existing files. All other
  Phase 4 dirty files, production source, model and migrations are preserved.
- Final source/whitespace/staging audit passed. Migration SQL is byte-for-byte
  identical to Phase 4. No staged files exist; backend HEAD remains external
  934c3c6, and the other four repositories retain their initial clean states/HEADs.
- Docker check remains blocked: docker_engine pipe is absent and the sandbox
  cannot read the user Docker configuration. No container test is claimed.

TRX and output evidence are under ignored artifacts/courier-phase5-*. No CI run,
coverage percentage, throughput/latency target, real JWT verification, broker effect
or shared deployment success is inferred from these results. Phase 4's live
MinIO/S3 acceptance limitations remain recorded in its report; failure reporting
does not require object storage, so no live storage installation is attempted here.

## Security, concurrency and documentation alignment

The existing principal adapter requires one authenticated identity and one stable
nonempty UUID sub; role claims must belong to that identity. Production continues
to register UnconfiguredIdentityHandler and fails closed. Isolated test principals
are not a production authentication feature. A client ID/role header, payload
courierId or Admin role does not grant failure mutation permission.

The database predicate checks current ownership under parcel-first and assignment
row locks, with a fresh ownership query after a parcel lock wait and retained EF
Version/xmin conditions. One expected version cannot accept two different failure
reasons. Principal/UpdateDeliveryStatus/key and canonical fingerprint prevent a
retry from overwriting the winning audit entry. Exact replay precedes state/version
checks but still requires the documented related assignment authorization; a
former Reassigned Courier receives 404. Same-actor Failed replay grants only the
original minimal receipt, not a new action or private GET access.

Missing/invalid reason and authorization/version/state failures do not leave a key
reservation or partial closure. All six injected SQL/commit failures roll back
parcel, assignment, reason/note, history, outbox and replay. Safe ProblemDetails
do not disclose SQL, private notes/reasons or another actor's currentVersion.
Failure releases the Active slot without rewriting assignment ownership/history or
creating a replacement. Dispatcher writers must honor the established shared lock/
version contract. The outbox remains pending until a separately integrated relay
publishes; there is no new RabbitMQ or notification path.

Canonical REST, matrix, SRS FR-R04, traceability, authorization, data architecture
and TC-R04 agree: Failed is a branch of UpdateDeliveryStatus and StatusChanged v1
is its sole event. There is no new business-contract mismatch to resolve. Proposed
architecture markers remain Proposed. Common StatusInput notes retain the existing
500-character bound; their presence is optional for Failed, including Other.

Inherited documentation gaps remain explicit: command receipts use the common
six-field Parcel projection; terminal same-actor replay is not spelled out in the
docs but preserves exact-result retry without reopening access; deployment docs
still describe implemented Delivery persistence as future. The backend README's
stale statement about future proof enforcement is corrected. The docs repository
is unchanged. No unsupported recovery/reference field is added to Customer Booking.

## Remaining dependencies and next phase

- Real Identity issuer/JWKS/signature/audience/expiry validation, Courier account/
  role provisioning and live JWT/revocation-bound acceptance.
- Delivery Courier/zone provisioning and dispatcher integration using the same
  current-assignment/version locking contract. No automatic allocation is added.
- Reviewed Delivery-only migration deployment and Neon role/TLS/grant verification;
  disposable PostgreSQL tests are not shared-deployment evidence.
- Existing outbox relay, durable consumer inbox and notification/webhook effects.
  Committed pending events are tested; publication and consumer outcomes are not.
- Docker-enabled container acceptance. Phase 4's private bucket/KMS/IAM/TLS and
  native-container proof acceptance remain dependencies for the overall workflow,
  even though Failed reporting has no storage prerequisite.
- React and Flutter Courier failure UI remain Phase 7. No new client routes,
  networking/auth layer or history screen is added in this backend checkpoint.

Next recommended phase: **Phase 6 — Courier delivery history (FR-R05)**, after an
explicit request, while keeping the mandatory live integration dependencies above
visible. Phase 6 must use retained historical assignments, not only current Active
rows; it has not been started. Stop after Phase 5. This agent has not staged,
committed, pushed, merged or modified unrelated work; external 934c3c6 is preserved.
