# Courier workflow: discovery and Phase 1 checkpoint

Inspected 28 October 2026. Scope is discovery and Phase 1 domain/persistence
only. Phase 2 requires a separate instruction. This report records implementation
evidence separately from the documentation's Proposed designs.

**Final status: Phase 1 foundation gate met.** After the user's contract
clarifications, the Release build, PostgreSQL tests, EF checks and host smoke
checks pass. Full solution: **626 passed, 0 failed, 0 skipped**. See the
[final follow-up verification](#final-follow-up-verification-and-phase-1-gate).
Phase 2 has not started. No public Courier feature is claimed complete yet.

## Confirmed follow-up decisions and implementation plan

The user has now explicitly confirmed:

- Assignment lifecycle: Active, Reassigned, Delivered, Failed, Cancelled; exactly
  one Active assignment per parcel. Status, not recency, identifies the current row.
- Final proof: at least one photo OR signature; both are accepted. This does not
  approve every Proposed ADR or implement proof upload/completion in Phase 1.

Rechecked the existing feature branch and all staged/unstaged/untracked changes
before this follow-up. Only the previous Phase 1 work is present. Preserve the
first Courier migration and add a second additive assignment migration.

Plan before assignment feature edits:

- Add Domain/AssignmentStatus.cs and CourierAssignment.cs using the documented
  assignments fields: IDs, service date, status and assignment/pickup/delivery
  timestamps. Preserve old rows, immutable ownership and before-pickup release.
- Extend CourierEntityConfigurations.cs and DeliveryDbContext.cs for assignments,
  local restrictive FKs, filtered unique Active parcel index, manifest/history
  indexes and PostgreSQL xmin concurrency (no extra application version column).
- Generate AssignmentsPersistence migration/designer and update the snapshot;
  include SQL guards retaining rows and preventing owner/history rewrites or
  resurrection of closed assignments. Do not add a dispatcher assignment process.
- Add AssignmentDomainTests.cs and AssignmentPersistenceTests.cs for lifecycle,
  real FK/check/uniqueness constraints, retained reassignment ownership, race
  rejection, migration upgrade and transactional rollback. Extend proof metadata
  tests to cover both types together without enabling completion.
- Update backend runbooks and this report to reflect the confirmed decisions and
  actual results. No endpoint, authentication, client, broker or storage adapter
  changes; stop at Phase 1.

The earlier discovery/partial-checkpoint sections below record the original
assessment. Their pending decisions are resolved by this explicit confirmation;
the final follow-up results supersede the original incomplete gate/results.

## Repository baseline and collaboration

| Repository | Branch / commit at discovery | Working tree | Existing implementation |
|---|---|---|---|
| backend | main / 884f576 | Clean, including staged/untracked files | Customer quotes/booking, shared Merchant booking construction/persistence, EF migration, tests |
| docs | main / d97fff4 | Clean | Proposed baseline 2.0 and reconciled booking/Neon contracts |
| infra | main / 075a8b4 | Clean | Host/Docker runner, explicit migration tasks, external Neon, RabbitMQ; no object store |
| frontend | main / c062b11 | Clean | React Router framework, TanStack Query, Tailwind, blank product routes; no session/API adapter |
| mobile | main / b819b42 | Clean | Flutter Customer booking, injected session/HTTP interfaces, tests; no Courier screens |

No AGENTS.md was found in the workspace or its ancestor directories. Backend
CONTRIBUTING.md and existing persistence/booking handoffs apply. Work is on
`feat/courier-workflow-persistence`, based on the inspected main commit. Local
origin/dev contains no commits absent from main. Remote-tracking refs are local
snapshots; no fetch, live collaborator activity, or team approval is inferred.
Nothing is committed, pushed, merged, reset, or applied to shared Neon.

Shared files at risk of later merge conflicts are DeliveryDbContext.cs,
DeliveryEntityConfigurations.cs, the EF snapshot, BookingHistory.cs and the
infrastructure README. Booking handlers, assembler, write helper, price models,
initial migration, Identity, clients and infra are preserved. Coordinate new
migration ordering and assignment state conventions with the assignment owner.

## Sources and verified requirements

Read the current local SRS, traceability, use cases, scope, assignment overview;
technical design, service catalog, data architecture, state matrix, ADR register;
REST/reference/routes, event catalog/schema and integration design; authentication,
authorization, data protection; testing strategy/cases; local setup/environment/
Compose guides; ER, assignment/proof and Courier notification sequence/activity
sources. The original assignment PDF is present under requirements/sources;
its repository-maintained page comparison distinguishes official requirements
from stronger team proposals. Subsequently extracted and read all four original
PDF pages using a temporary PDF.js tool outside the repositories: page 2 confirms
daily assigned work, note OR proof (recipient name/photo examples), failure
reason, authentication and a couriers ER entity. No assignment-status values or
mandatory final photo/signature combination are specified by the PDF.

| Requirement | Actual definition | Remaining implementation after foundation |
|---|---|---|
| FR-R01 Must | Selected service day, default today, active overdue assignments; pickup/drop-off details | Authorized manifest/detail query and clients |
| FR-R02 Must | Current Courier stages with private notes; final status via proof operation | Central policy, authenticated handlers, version/history/outbox transaction |
| FR-R03 Must | Photo or signature proof | Private storage, content validation, completion, reconciliation and clients |
| FR-R04 Must | Failed with reason code and optional notes | Authorized versioned failure handler and clients |
| FR-R05 Should | Courier completed/failed records | Historical assignment query, pagination and clients |

The canonical statuses already exist: Created, Assigned, PickedUp, InTransit,
OutForDelivery, Delivered, Failed, Cancelled. Proposed operational chain is
Assigned -> PickedUp -> InTransit -> OutForDelivery -> Delivered. Current Courier
may fail from any active stage with RecipientUnavailable, AddressUnreachable,
Damaged or Other. Private stage notes have 1-500 characters. Failed, Delivered
and Cancelled are terminal. Reassignment is before pickup only. No state policy
or mutation endpoint is implemented in Phase 1.

All changes require current ownership and expectedVersion at commit; changing
status, incrementing version, appending history and adding one StatusChanged v1
outbox record must share a transaction. Broker delivery is later relay work.
No private notes, recipient/contact/address data, proof or storage URLs belong
in StatusChanged. Existing event schema and SQL event-provenance uniqueness
are reused. History's approved schema has no parcel-version column; do not add
one speculatively. Version provenance already lives in the retained outbox.

## API contracts verified, not implemented here

| Operation | Contract | Authorization / input |
|---|---|---|
| CourierAssignments | GET /api/v1/couriers/me/assignments | Courier JWT sub; date, includeOverdue, page/pageSize |
| GetParcel | GET /api/v1/parcels/{id} | Authorized private parcel including current assignment |
| UpdateDeliveryStatus | PUT /api/v1/deliveries/{id}/status | Current Courier; status, expectedVersion, stage notes or failureReason; Idempotency-Key |
| CompleteDeliveryWithProof | POST /api/v1/deliveries/{id}/pod | Current Courier; multipart photo/signature, recipientName <=120, expectedVersion; Idempotency-Key |
| CourierHistory | GET /api/v1/couriers/me/history | Courier self; page/pageSize |
| DownloadProof | GET /api/v1/parcels/{id}/pod | Owner Customer/Admin, Delivered; private streamed bytes |

Pagination defaults 1/20, maximum pageSize 100. No client courierId authorizes
access. 401 invalid credential, 403 wrong role, 404 missing/foreign resource,
409 stale version/invalid state. Future proof accepts decoded JPEG/PNG <=5 MiB
each (10 MiB combined); opaque generated storage references stay private.

## Existing database, authentication and integration

At discovery, Delivery owned eight existing tables: delivery_zones, pricing_tiers, quotes,
parcels, price_snapshots, booking_history, idempotency_keys, outbox_messages.
One initial EF migration (20261023142857_InitialBookingPersistence) and snapshot
existed. Parcel.Version is an application concurrency token; quotes/tariffs use
xmin. Immutable snapshots/booking details, quote ownership, retained replay
responses and outbox provenance are already enforced with SQL triggers.

BookingHistory already has actor/time, from/to states, change_reason and
delivery_note, with restrictive parcel FK and one initial row per parcel.
Creation factories already write history and a redacted outbox payload. Reuse
these entities; append-only protection is the missing history safeguard.

Delivery's ICurrentCustomer/ClaimsCurrentCustomer consumes authenticated UUID
sub and Customer roles. UnconfiguredIdentityHandler always declines production
authentication. JWT signature/issuer/audience/JWKS validation is NOT implemented.
Only isolated test hosts supply fictional authenticated principals. Phase 1 adds
no authentication bypass or public/provisioning/assignment API.

Couriers are Delivery operational profiles, not duplicated Identity accounts:
their ID references the verified Identity UUID without cross-database SQL/FKs.
Zone membership is local. Capacity and assignment writes need atomic locking
in the later dispatcher feature; a profile by itself grants no parcel access.

There is no object-storage package, adapter, credential or provisioned service.
ER Protected Proof Storage proposes MinIO/S3, while current service catalog and
Compose explicitly defer provisioning. Store metadata only after a future
server adapter confirms staged private object persistence; upload status is
implicit in committed metadata, not a newly invented ER column. Binary objects,
upload verification/cleanup and final proof-policy enforcement are later work.

Existing idempotency persistence/SQL permits only stored 201 booking responses;
Courier 200 replay requires a separately reviewed compatible extension in the
mutation phases. Do not silently reuse it as though arbitrary status responses
are already supported. Identity JWT issuance, assignment owner, object storage
and relay are independent dependencies; none is manufactured here.

## Documentation gaps and discrepancies

1. architecture/decisions/README.md: all ADR-001..005/DEC-01..12 remain Proposed;
   architecture/parcel-state-machine.md explicitly awaits DEL/REQ/SEC approval.
   User confirmation of Neon/tariff selection does not accept other decisions.
2. design/diagrams/er/README.md Delivery assignments defines status but no allowed
   values, current predicate, release timestamp or reassignment lifecycle. The
   state matrix and assignment diagrams require exactly one current assignment
   and retained old ownership. The user resolved this gap in the follow-up:
   Active/Reassigned/Delivered/Failed/Cancelled, with a unique Active parcel row.
   Never use latest row. The documentation itself remains Proposed/unchanged.
3. SRS FR-R03, REST ProofInput and state matrix say photo OR signature (both
   accepted); ER Assignment-source clarification says strict final proof is a
   team proposal; assignment page comparison allows a note or proof. Requested
   explicit confirmation of final at-least-one versus both policy. The user
   confirmed at least one photo or signature, accepting both. Metadata supports
   all three combinations; actual completion enforcement remains Phase 4.
4. deployment/local-setup.md Host mode says Identity migrations run automatically;
   infra README and actual runner describe explicit migration tasks. Its Current
   limitations and deployment/docker-compose.md also say Delivery lacks DB
   adapters/migrations, contradicted by backend 884f576 and current infra tasks.
   Follow actual owning code and explicit migrations; leave docs repo unchanged.
5. No approved proof-object retention/deletion policy exists. Proposed cleanup is
   only unreferenced staged objects older than 24h, protecting committed refs.
   No storage integration or runtime test evidence is supplied by docs.

## File-level implementation and test plan (before feature edits)

Safe independent work:

- Domain/Courier.cs, CourierZone.cs: operational fields from ER, UUID references,
  positive capacity/version, local zone membership.
- Domain/ProofType.cs, ProofMetadata.cs: Photo/Signature metadata from ER,
  parcel, private opaque storage_ref, recipient_name, uploaded_by/uploaded_at;
  no blobs, credentials, public URLs or completion policy.
- Infrastructure/Persistence/CourierEntityConfigurations.cs: focused EF mappings,
  restrictive FKs, unique courier/zone pair, unique storage ref and query indexes.
- DeliveryEntityConfigurations.cs: call new mappings before existing snake-case
  convention. DeliveryDbContext.cs: new sets and append-only history guard.
- A new additive CourierPersistenceFoundation migration/designer and EF snapshot:
  new tables/checks/FKs/indexes, SQL append-only booking_history trigger. Never
  edit the original migration or reset a database.
- tests/.../Courier/CourierDomainTests.cs and CourierPersistenceTests.cs: real
  PostgreSQL mappings/FKs/uniqueness, metadata persistence/privacy, history
  append-only protection via EF/direct SQL, existing parcel version races,
  migration upgrade preserving a booking, no pending model changes.
- Infrastructure/README.md and this report: exact scope, dependencies, results.

Assignment entity/status, mapping, filtered current-uniqueness index and tests
were added after explicit lifecycle clarification. Additional release fields or
history/version fields require a documented contract, not inference. No changes
to API routes, authentication, clients, assignment algorithm, notifications or
storage adapter in Phase 1.

Run repository-pinned SDK restore, Release solution build/test and foundation
smoke script; EF tool restore, script generation and pending-model check. Reuse
PostgresFixture's private disposable cluster, not shared Neon. Add real concurrent
write tests with separate contexts and a barrier. Missing infrastructure is a
blocked check, never a passing or silently skipped test.

## Phase 1 implementation summary and actual files

Implemented Phase 1 domain/persistence, including the confirmed assignment
lifecycle. Reused Parcel, its existing
Version concurrency token, BookingHistory, OutboxMessage and the original
Booking migration. No API routes or request/response contracts changed. No
identity, account, dispatcher assignment algorithm, relay, notification, client
or object-storage implementation was added. The original contract blockers are
resolved; final verification for the completed foundation is recorded below.

All changed files are in **swiftdrop-backend**:

- Delivery Domain: Courier.cs, CourierZone.cs, ProofType.cs, ProofMetadata.cs,
  AssignmentStatus.cs, CourierAssignment.cs.
- Delivery Infrastructure/Persistence: CourierEntityConfigurations.cs,
  DeliveryDbContext.cs, DeliveryEntityConfigurations.cs;
  Migrations/20261028110532_CourierPersistenceFoundation.cs, its Designer.cs,
  Migrations/20261028112000_AssignmentsPersistence.cs, its Designer.cs,
  and DeliveryDbContextModelSnapshot.cs.
- Delivery tests: Courier/CourierDomainTests.cs, Courier/CourierPersistenceTests.cs,
  Courier/AssignmentDomainTests.cs, Courier/AssignmentPersistenceTests.cs.
- Documentation: README.md, Delivery Infrastructure/README.md, this report.

Database changes: four new tables. couriers.id is supplied, not generated;
capacity/version must be positive; Courier.Version is a concurrency token.
courier_zones has a composite PK and restrictive FKs to the two local tables.
proof_metadata has a restrictive parcel FK, Photo/Signature check, nonblank
recipient (max 120), actor UUID and private-ref checks, unique storage reference
and parcel/time/id index. Existing history now rejects UPDATE/DELETE through EF
and the new SQL trigger. No extra history/version/upload-status fields invented.
No shared/Neon migrations were run. Generated SQL is in ignored
`artifacts/courier-phase1-with-assignments.sql` for review.

assignments stores the ER's parcel/Courier IDs, service_date, confirmed status,
assigned_at, picked_up_at and delivered_at. Restrictive local FKs, UTC timestamp
checks, before-pickup release checks and xmin concurrency protect the records.
`ux_assignments_current_parcel` permits one Active assignment per parcel;
service-day and history indexes support later Courier queries. A SQL guard
requires new rows to begin Active, retains all historical rows, fixes ownership/
schedule, preserves pickup time and rejects closed-row resurrection. No assignment
version, release timestamp, dispatcher algorithm or provisioning API was invented.
Future reassignment handlers must flush the old release before inserting the
replacement in the same transaction; failure rolls back both. Parcel-version
and assignment authorization inside operational handlers remain Phase 3 work.

Security boundaries: operational Courier IDs are references to verified Identity
users, not local account replicas. No caller gains parcel ownership from a
profile. Existing production authentication still fails closed. Metadata stores
no blobs/credentials/public URLs and does not advance status; actual storage
privacy and assignment authorization are not claimed as implemented. Existing
outbox serialization remains unchanged and private metadata stays outside it.

## Initial partial-checkpoint validation evidence

Default dotnet had runtime 10.0.12 but no SDK. Downloaded repository-pinned SDK
10.0.301 to a temporary directory, retaining all project/tool pins. Set only
process-local DOTNET_ROOT/PATH/DOTNET_CLI_HOME/telemetry variables for commands.
Restored via a temporary NuGet configuration because the sandbox could not read
the user's roaming NuGet.Config. No project packages were added. Temporary
PDF.js 6.4.299 extracted the original PDF outside the repositories; SHA-256
matches B19457C735E4F3608ADF54A5C7D03C06114E021367C66F01366F01C92A0BF5B2.

From backend root (using that temporary SDK):

```powershell
dotnet restore SwiftDrop.sln --configfile "$env:TEMP/swiftdrop-courier-nuget.config" --disable-parallel
dotnet build SwiftDrop.sln --no-restore --configuration Release
dotnet tool restore --configfile "$env:TEMP/swiftdrop-courier-nuget.config"
dotnet ef migrations add CourierPersistenceFoundation --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --output-dir Infrastructure/Persistence/Migrations
# Added the reviewed SQL append-only history trigger to the new migration, then rebuilt.
dotnet test tests/SwiftDrop.Delivery.Tests --no-build --configuration Release --filter FullyQualifiedName~CourierDomainTests
dotnet ef migrations script --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --output artifacts/courier-phase1.sql
dotnet ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
dotnet test SwiftDrop.sln --no-build --configuration Release --logger 'trx;LogFilePrefix=courier-phase1-final' --results-directory artifacts/courier-phase1-final
./scripts/verify-foundation.ps1 -Configuration Release
```

PostgreSQL tests use installed PostgreSQL 18, private random-port clusters under
temporary `swiftdrop-courier-tests-elevated`; the external database fallback
variable was removed for this process. No shared Neon or existing PostgreSQL
service was used. The migration-upgrade test creates and removes its own named
schema inside that disposable cluster, applies the original migration, saves a
real booking, then applies the additive migration and verifies retained records.

- Restore/tool restore: passed; pinned dotnet-ef 10.0.9 retained.
- Final Release build: passed, 0 warnings/errors.
- Initial new domain-only run: 12 passed, 0 failed/skipped.
- Migration script generation: passed; reviewed additive DDL and SQL history trigger.
- Pending model check: passed, no changes since last migration.
- Foundation smoke: passed all 12-project/reference checks, five host startup/
  health checks, 15 absent-feature/internal/readiness probes, 401 fail-closed
  booking/quote probes and private listener check; script stopped its own hosts.
- Final complete solution suite: **599 passed, 0 failed, 0 skipped**, including
  **25 new Courier cases** (12 domain and 13 PostgreSQL integration cases).

| Test assembly | Passed | Failed | Skipped |
|---|---:|---:|---:|
| SwiftDrop.Delivery.Tests | 508 | 0 | 0 |
| SwiftDrop.Gateway.Tests | 68 | 0 | 0 |
| SwiftDrop.Identity.Tests | 12 | 0 | 0 |
| SwiftDrop.Merchant.Tests | 6 | 0 | 0 |
| SwiftDrop.Notification.Tests | 5 | 0 | 0 |
| Total | 599 | 0 | 0 |

Final TRX files are in ignored `artifacts/courier-phase1-final/`; sanitized
console output is in `artifacts/courier-phase1-final-output.txt`. The Delivery
run took 2m14s. Coverage was not collected. Existing regression scenarios cover
fee estimates, quote/booking validation, tracking-number generation, owner and
role denial, idempotency/contention/rollback/replay, immutable snapshots,
history/outbox persistence and Merchant construction/write compatibility.
The new tests verify local FK/unique/check constraints, private proof metadata
round-trips without a status/event side effect, append-only history via EF and
SQL, one-winner Courier/Parcel concurrency, and preservation of a preexisting
booking across the additive migration. The checked-in and docs StatusChanged
schemas have identical SHA-256 54B78C4AA3867C31447E9B9FA3BBC85E87CEE27897F67BE7BE923B0121CB523D.
Final diff whitespace check passed; the other four repositories remain clean.

Record of earlier failures: the first sandbox execution failed host tests due to
Windows EventLog denial and initdb restricted-token/path permissions. Running
outside the sandbox resolved those environment failures. The first complete
unsandboxed run had 3 new test failures, with all existing regression tests
passing: corrected two unsupported text-LIKE queries over JSONB to use the
known event ID and count, and corrected restrictive-delete SQLSTATE expectation
from ForeignKeyViolation to RestrictViolation. Those were test bugs, not changes
to the production constraints. Retained initial TRX/output separately in ignored
artifacts; final results must be assessed independently.

Blocked/unexecuted integration: Docker image/build/start (daemon unavailable),
live Neon deployment, real JWT issuance/verification, object-store access/private
bucket/upload/cleanup and broker publication/consumers. Courier endpoints,
state-machine handler and React/Flutter tests belong to
unfinished/future work and were not run. No coverage percentage or full FR-R01-05
acceptance is claimed.

## Dependencies, remaining work and next checkpoint

- Identity owner: issue/validate real Courier JWT and supply verified operational
  UUIDs later. No Identity DB access or production test authentication added.
- Booking/Merchant owner: existing quote, confirmation, retained snapshots,
  idempotency and shared booking construction/persistence are preserved. Coordinate
  shared context/snapshot/migration ordering before integration.
- Assignment owner: the user confirmed lifecycle and current-row predicate;
  persistence and concurrency tests are implemented. Dispatcher availability,
  zone/capacity locking and authorized allocation remain with that feature owner.
- Proof policy: photo OR signature, both accepted, explicitly confirmed by user.
  Completion enforcement remains Phase 4. Object-store provisioning/
  credentials/adapter and staged reconciliation remain separate dependencies.
- Notifications owner: existing StatusChanged v1 outbox is reused; no new publish
  path, private fields in events or direct notification delivery added.

The foundation now includes assignments. Final follow-up verification is below.
Phase 2 remains explicitly out of scope until separately requested. The other four
repositories remain on their original branches and clean; backend changes are
uncommitted on feat/courier-workflow-persistence.

## Final follow-up verification and Phase 1 gate

The confirmed lifecycle/proof-policy decisions removed the original blockers.
Domain/persistence is complete for Phase 1, with four added tables across two
additive migrations. The initial Booking migration and existing Booking/Identity/
Merchant handlers were not changed. All original repository working trees were
rechecked; only the 21 expected backend source/test/doc files differ from main.
The other four repositories remain clean on their original branches.

Additional exact commands executed from backend root using the same temporary
pinned SDK/process environment described above:

```powershell
dotnet build SwiftDrop.sln --no-restore --configuration Release
dotnet ef migrations add AssignmentsPersistence --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --output-dir Infrastructure/Persistence/Migrations
# Added the new migration's assignment history/lifecycle SQL trigger, then rebuilt.
dotnet build SwiftDrop.sln --no-restore --configuration Release
dotnet test tests/SwiftDrop.Delivery.Tests --no-build --configuration Release --filter 'FullyQualifiedName~AssignmentDomainTests|FullyQualifiedName~AssignmentPersistenceTests' --logger 'trx;LogFilePrefix=courier-assignments' --results-directory artifacts/courier-assignments
dotnet ef migrations script --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --output artifacts/courier-phase1-with-assignments.sql
dotnet ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
dotnet test SwiftDrop.sln --no-build --configuration Release --logger 'trx;LogFilePrefix=courier-phase1-complete' --results-directory artifacts/courier-phase1-complete
./scripts/verify-foundation.ps1 -Configuration Release
git diff --check
```

| Check | Actual result |
|---|---|
| Release solution build | Passed; 0 warnings/errors |
| New assignment/proof-combination cases | 27 passed (12 domain, 15 PostgreSQL); no failures/skips |
| Migration script | Generated and reviewed; additive assignment table/FKs/checks/indexes/trigger |
| EF pending model changes | None |
| Foundation smoke | Passed all five host health checks, 12-project reference audit, gateway/private/fail-closed probes; its processes stopped |
| Final diff/file-scope checks | Passed; original Booking migration, application/presentation, Identity and Merchant sources unchanged |

| Final complete suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| SwiftDrop.Delivery.Tests | 535 | 0 | 0 |
| SwiftDrop.Gateway.Tests | 68 | 0 | 0 |
| SwiftDrop.Identity.Tests | 12 | 0 | 0 |
| SwiftDrop.Merchant.Tests | 6 | 0 | 0 |
| SwiftDrop.Notification.Tests | 5 | 0 | 0 |
| Total | 626 | 0 | 0 |

This is **52 new Courier cases** in total: the initial 25 plus 27 follow-up
cases. Delivery completed in 2m19s. Final TRX evidence is in ignored
`artifacts/courier-phase1-complete/`, with console output in
`artifacts/courier-phase1-complete-output.txt`. Tests used only fixture-owned
disposable PostgreSQL; no shared Neon migrations or writes occurred. No packages
were added; the earlier successful restore remains applicable.

Assignment tests verify both local FKs, unique current parcel ownership,
service dates, pickup/delivery times, retained old owners independent of row
recency, terminal records, invalid SQL writes, immutable closed records and
restrictive deletion. Real races verify one winner for concurrent Active inserts
and for pickup versus release using xmin. A failed replacement rolls back its
release and leaves the original current owner. Upgrade tests preserve existing
booking/proof/Courier records. Photo-only, signature-only and both-type metadata
persist; metadata does not advance a parcel or emit a completion event.

**Gate:** Phase 1 builds, passes relevant persistence/concurrency/constraint
tests and the complete existing Delivery/solution regressions. Stopped here.
API changes: none. Courier role/resource authorization, parcel transition
orchestration with history/outbox, upload verification and final-proof enforcement
remain later-phase work. Assignment row concurrency is a persistence foundation,
not evidence of a completed authorized status handler.

Docker image/start verification, live Neon deployment, real Identity JWT flows,
private object-store/broker integration and React/Flutter Courier flows remain
unexecuted. Docker's daemon was unavailable at discovery. These limitations do
not establish full FR-R01–R05 acceptance, and no coverage percentage is claimed.
Next suggested phase is Phase 2 manifest/details, only after an explicit user
instruction. No commits, pushes, merges or external messages were performed.
