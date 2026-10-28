# Courier Phase 6: historical deliveries

29 October 2026. Phase 6 only, FR-R05 / TC-R05. No React/Flutter screens,
assignment algorithm, new mutation, private download, commit, push or merge.
The backend Phase 6 checkpoint is complete under isolated trusted principals and
disposable PostgreSQL; live integration/deployment acceptance remains explicit below.

## Inspection and contract before implementation

All repositories were reinspected. Backend is clean on
feat/courier-workflow-persistence at b9df209; that external commit contains the
Phase 5 acceptance/docs changes, not a new production failure API. Its source and
actual TRX counters verify 961 passed, zero failed/skipped. Docs/main d97fff4,
infra/main 075a8b4, frontend/main c062b11 and mobile/main b819b42 remain clean.
No AGENTS.md applies; CONTRIBUTING applies. Starting Delivery/test/docs/script
file hashes are recorded in ignored artifacts/courier-phase6-starting-files.json.
No remote fetch or inference about remote teammate activity is made.

Read original master prompt Phase 6; SRS FR-R05, traceability/TC-R05; REST operation,
PageQuery and Parcel projection; routes.json; state matrix; authorization; retained
assignment/data/ER model; and the existing read/status/proof/failure implementation.
The documentation remains Proposed. The user-approved assignment lifecycle and
photo-OR-signature policy are preserved.

Verified contract and scope decisions:

- GET /api/v1/couriers/me/history, operation CourierHistory, human Courier self,
  PageQuery, ParcelPage, 200. No caller Courier ID or operations-role override.
- Page defaults 1, pageSize defaults 20 and is 1-100. Stable createdAt/id ordering,
  matching existing descending manifest convention. Unknown/duplicate parameters,
  invalid integers and overflowing offsets return 400 after authorization.
- SRS explicitly limits this view to completed and failed records. Select retained
  Delivered/Failed assignments belonging to the caller and matching parcel state;
  do not query only current Active assignments, which terminal writes release.
- Reassigned-away rows do not grant access to another Courier's later terminal
  outcome. A Courier assigned back and actually completing/failing gets one parcel
  record, not one per old assignment. Cancelled and active work are excluded.
- Return the established six common Parcel fields/price snapshot only. The self
  history route does not reopen shared private GET access or reveal recipient,
  address, previous Courier IDs, notes/reasons, proof refs or timeline audit data.
  Explicit terminal summaries follow FR-R05; private detail ownership is unchanged.
- Only documented PageQuery is accepted. No status/date/actor filter or new terminal
  timestamp/assignment DTO field is invented. Fixed terminal and ownership filters
  implement the requested appropriate filtering.

Existing code has manifest and shared detail reads but no CourierHistory route.
Reuse trusted current-user and read errors/private-cache metadata, existing Parcel/
Booking price DTO, PostgreSQL DbContext and read transaction pattern. Count/page
must share one repeatable-read snapshot. This is read-only: no version, assignment,
proof, history, replay, migration or outbox write is required.

## Exact file plan before feature edits

- Application/DeliveryReads/CourierHistoryContracts.cs (new): PageQuery input,
  history page and focused storage port using existing ParcelBookingResponse.
- Application/DeliveryReads/DeliveryPagination.cs (new): extract the existing
  manifest page validation unchanged; reuse for both reads.
- Application/DeliveryReads/CourierHistoryHandler.cs (new): trusted Courier-only
  authorization, strict pagination and safe dependency/cancellation behavior.
- DeliveryReadHandler.cs: use the common pagination helper without changing
  manifest/service-day/detail behavior or the existing read storage interface.
- Infrastructure/DeliveryReads/PostgresDeliveryReadStore.cs: implement focused
  history port with historical terminal ownership EXISTS predicate, minimal SQL
  projection, read-only repeatable-read transaction and count/page consistency.
- Presentation/DeliveryReadEndpoints.cs and Program.cs: exact route and scoped DI;
  reuse existing read authentication errors/private cache behavior.
- New CourierHistory application/authentication/PostgreSQL API tests for terminal
  actors, reassignment/assign-back, stable pages, privacy, no writes, snapshot race,
  cancellation/dependency failure and real status/proof completion compatibility.
- StatusTestScenario.cs: add an optional known replacement actor to the existing
  test-only dispatcher helper for assign-back coverage; its default behavior stays.
- FoundationTests.cs and scripts/verify-foundation.ps1: history denial probe alongside
  existing manifest/detail/write probes. README, infrastructure runbook, focused
  history handoff and this report with actual evidence and documentation limits.

Run restore/Release build, focused and full suites, EF model/script checks and real
host/Gateway smoke. Preserve all Booking, Identity integration, mutations, retained
history/outbox, migrations and proof/cleanup behavior. Live Identity/storage/broker/
Docker acceptance limitations remain explicit; this Should feature does not imply
that outstanding mandatory deployment integrations are complete.

## Actual results

The verified history route, safe projection, historical ownership and consistent
paging are implemented. All focused and full regression checks passed.

## Functionality, API and database changes

The exact documented history GET is registered with the existing read metadata,
authorization/error formatting and private/no-store headers. The focused handler
checks trusted subject and Courier role before parsing pagination or resolving
storage. Only page/pageSize are accepted. Empty, negative, fractional, oversized,
duplicate, unknown or offset-overflow input fails safely; no actor/date/status
selector is added. Default/maximum page behavior matches the existing manifest.

The SQL predicate requires the caller's retained Delivered/Failed assignment and
the corresponding parcel status. Current Active ownership is not required, and
reassigned-away rows never grant a replacement's outcome. Assign-back produces one
parcel through EXISTS rather than duplicate assignment rows. Active, unassigned,
Cancelled and foreign records are absent. Actual status/proof/failure writes create
the terminal fixtures used by the API tests; the endpoint does not infer outcomes
from a latest assignment timestamp or create synthetic history.

The page returns the existing six common Parcel fields: id, trackingNumber, status,
version, priceSnapshot and createdAt. It contains no private contact/address,
service date, failure reason/note, other actor ID, proof URL/key, replay or audit
timeline. Historical summary permission does not grant shared private GetParcel
access. The SQL explicitly selects only common Parcel/price columns; contacts,
private history, proof and outbox tables are not loaded for projection.

Count and page share one read-only REPEATABLE READ transaction, ordered by parcel
createdAt/id descending. A completion/failure committed between those queries is
seen by neither until a subsequent request. Each request has its own snapshot;
there is no undocumented cross-request cursor guarantee. AsNoTracking projections
leave the EF tracker empty, and cancellation propagates without another identity
or retry. Provider/configuration errors map to safe read 503 responses.

API addition: GET /api/v1/couriers/me/history (CourierHistory), PageQuery ->
ParcelPage/200. Errors 400/401/403/503 reuse existing codes. No alternate history,
detail, download or mutation route is added. Database changes: **none**; the model,
all five migrations, existing indexes, assignment/status/history/proof/replay
constraints and outbox schema are unchanged. No shared DDL is applied.

## Files changed

17 backend files (8 new, 9 modified); no other repository is changed. Paths in
the first table are relative to src/Services/Delivery/SwiftDrop.Delivery.Api/.

| File | Change |
|---|---|
| Application/DeliveryReads/CourierHistoryContracts.cs (new) | Page input/query, minimal page using existing ParcelBookingResponse and focused history port |
| Application/DeliveryReads/CourierHistoryHandler.cs (new) | Courier-self identity/role guard, pagination and safe error/cancellation behavior |
| Application/DeliveryReads/DeliveryPagination.cs (new) | Existing manifest pagination extracted unchanged for shared use |
| Application/DeliveryReads/DeliveryReadHandler.cs | Reuse pagination helper; preserve service-day/detail behavior and existing read port |
| Infrastructure/DeliveryReads/PostgresDeliveryReadStore.cs | Historical terminal ownership, minimal projection and read-only consistent page/count |
| Presentation/DeliveryReadEndpoints.cs | Exact history GET, strict PageQuery, existing metadata/problems |
| Program.cs | Scoped handler/history-port registration with deferred DB resolution |
| Infrastructure/README.md | Ownership, snapshot, privacy and deployment handoff |

| Backend-relative file | Change |
|---|---|
| tests/SwiftDrop.Delivery.Tests/Courier/CourierHistoryApplicationTests.cs (new) | Authorization ordering, trusted subject, bounds/offsets, dependency/cancellation |
| tests/SwiftDrop.Delivery.Tests/Courier/CourierHistoryAuthenticationTests.cs (new) | Direct deployed-host denial, wrong roles/subjects/identity role mixing and query bypass attempts |
| tests/SwiftDrop.Delivery.Tests/Courier/CourierHistoryApiTests.cs (new) | Real terminal workflows, merchant/customer provenance, reassignment/assign-back, stable paging, no writes, SQL minimization/read-only transaction and count/page race |
| tests/SwiftDrop.Delivery.Tests/Courier/StatusTestScenario.cs | Optional known replacement actor on the existing test-only reassign helper; default preserved |
| tests/SwiftDrop.Delivery.Tests/FoundationTests.cs | Additional history authentication probe |
| scripts/verify-foundation.ps1 | Real Gateway history GET denial/private-cache probe alongside every prior check |
| README.md | Current history behavior and checkpoint/runbook links |
| docs/courier-history.md (new) | Verified query, summary fields, ownership, snapshot, errors and limits |
| docs/courier-phase-6-assessment.md (new) | Before-edit assessment, implementation, actual results and integration dependencies |

No dependency, other service, Customer Booking, Identity claims/authentication,
mutation handler, domain entity, persistence model/migration, private-detail
policy, proof storage/cleanup or outbox writer is modified. Shared read pagination,
adapter/routing/DI are the deliberate Delivery integration review points.

## Builds and tests

Pinned SDK 10.0.301 and EF tooling 10.0.9, PostgreSQL 18 fixture-owned private
clusters on random loopback ports. Test processes clear
SWIFTDROP_TEST_DELIVERY_DATABASE and use the existing owned temporary root. Public
NuGet restore uses the existing process-local configuration; no package version,
global setting, shared Neon database or live bucket/broker is changed.

```powershell
dotnet restore SwiftDrop.sln --configfile "$env:TEMP/swiftdrop-courier-nuget.config" --disable-parallel
dotnet build SwiftDrop.sln --no-restore --configuration Release
dotnet test tests/SwiftDrop.Delivery.Tests --no-build --configuration Release --filter 'FullyQualifiedName~CourierHistory|FullyQualifiedName~DeliveryReadApplicationTests' --logger 'trx;LogFilePrefix=courier-phase6-targeted' --results-directory artifacts/courier-phase6-targeted
dotnet test SwiftDrop.sln --no-build --configuration Release --logger 'trx;LogFilePrefix=courier-phase6-final' --results-directory artifacts/courier-phase6-final
dotnet ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
dotnet ef migrations script --idempotent --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --output artifacts/courier-phase6-migrations.sql
./scripts/verify-foundation.ps1 -Configuration Release
```

- Restore passed. Release builds passed with zero warnings and zero errors.
- Focused history/shared-pagination suite: **56 passed, 0 failed, 0 skipped**,
  duration 27 seconds. This includes 47 new history cases and nine existing
  application read cases; the added foundation probe is in the full solution.
- Full solution regression: **1,009 passed, 0 failed, 0 skipped**: 918 Delivery
  (5m23s), 68 Gateway, 12 Identity, 6 Merchant and 5 Notification. All 961 previous-
  phase cases and 48 added cases passed (47 history cases plus one foundation probe).
  No runtime test failed or was suppressed during this phase.
- EF model check passed with no pending changes. Generated idempotent SQL is
  byte-for-byte identical to Phase 5; no migration was generated/applied.
- Actual host/Gateway smoke passed: 12-project reference/cycle checks, all five
  host startup/health checks, 15 missing/internal/readiness probes, previous
  Booking/quote/status/failure/proof checks, manifest/detail plus new history GET
  authentication/private-cache checks and private Delivery listener. The script
  stopped every host it started.
- Docker check remains unavailable: missing docker_engine pipe and denied sandbox
  access to the user's Docker configuration. No image/start/native-container test
  is claimed. This read endpoint requires no live storage/broker test.
- Final starting-file audit verified only the nine planned existing files changed;
  every other baseline file is byte-for-byte preserved. Diff whitespace, unchanged
  migrations/SQL and staging audits passed. Backend remains at b9df209 on the same
  feature branch, with no staged files; other repositories retain their initial
  clean states/HEADs. No new teammate overlap or external commit appeared in this run.

The history SQL tests confirm an empty tracker, actual transaction_read_only=on,
RepeatableRead isolation and absence of contact/history/proof/key/outbox columns
from both count/page commands. Another connection commits a real failure between
the two queries: the in-flight page remains consistent, then the next read sees
the terminal record. State snapshots verify no read-side changes to parcel,
assignment, history, events, replay, proof, quote/snapshot counts. An already
cancelled provider call makes no writes. Reassignment and actual completion/failure
tests verify former-owner exclusion and correct terminal actor; assign-back tests
verify deduplication. The Cancelled fixture represents the documented cancellation
atomically and does not add a production cancellation API.

Artifacts/TRX are under ignored artifacts/courier-phase6-*. No CI execution,
coverage percentage, scale/latency target, live JWT verification or deployment
success is inferred from these checks.

## Security, concurrency and documentation alignment

Production continues to use UnconfiguredIdentityHandler; real Identity token
verification is not introduced or bypassed. The existing claims adapter requires
one authenticated identity and one nonempty UUID sub, with roles from that same
identity. Wrong-role and forged/unsigned/deceptive identity requests fail before
data access. The history route is Courier-self only; operations privileges do not
become an arbitrary Courier selector. Tests verify unauthenticated Development and
Production hosts, invalid subjects, role mixing and query identity override attempts.

Ownership filtering is embedded in both SQL queries, not a separate authorization
check followed by unscoped data access. Matching terminal assignment/parcel status
keeps a former Reassigned actor from seeing a replacement's later result. The
unique Active slot and closed lifecycle are unchanged. EXISTS handles assign-back
without duplicate summaries. No outbox, replay key, proof reference or private
history is required to authorize this collection. The unchanged shared GetParcel
guard still rejects closed Couriers while allowing its documented owner/operations
roles. A collection item does not confer a new download/detail permission.

The read-only transaction prevents accidental request-side DML and repeatable-read
isolation prevents count/page disagreement. Legitimate terminal writers continue
with their established parcel-first/assignment locks and optimistic version
checks; history introduces no writer lock, mutation or retry. In-flight reads use
their authorized snapshot; separate later requests see newly committed records.
Stable ordering is within that snapshot/fixed dataset, not a cursor guarantee
across changing page requests. Raw SQL errors are not exposed in ProblemDetails.

Documentation gaps/interpretations are recorded rather than silently expanding
access. REST's CourierHistory route/SRS FR-R05 explicitly authorize self terminal
records, while the generic private parcel/history/SSE table refers to current
assignment. They are reconciled by a separate minimal terminal summary, preserving
the existing private-detail rule. PageQuery/ParcelPage do not define a detailed
historical projection or reassigned-away example: the six common fields and the
actual terminal actor follow FR-R05's *their completed and failed records*. Old
Reassigned rows do not authorize a replacement's private/current outcome.

The master prompt's appropriate filtering is implemented with fixed terminal and
ownership predicates. Canonical PageQuery does not authorize new status/date
filters, so none are invented. createdAt stays parcel creation time; no completion
timestamp field is silently substituted. There is no dedicated Courier-history
use-case/sequence in the docs checkout; SRS, REST/routes and TC-R05 supply its
explicit contract. Existing deployment docs still describe Delivery adapters as
future, as already reported. The docs repository and Proposed approval markers
are unchanged; implementation is not a team/lecturer architecture acceptance.

## Remaining integration dependencies and next checkpoint

- Verified Identity issuer/JWKS/signature/audience/expiry handling and live Courier
  role provisioning/revocation-bound tests. Production endpoints still fail closed.
- Delivery Courier/zone provisioning and dispatcher/status/proof integration that
  preserve retained lifecycle/version ownership. No allocation API is introduced.
- Reviewed Neon migration deployment, owning role/TLS/grant verification and
  production data/index/load acceptance. No shared deployment was performed.
- Docker-enabled image/start checks and outstanding mandatory private proof bucket/
  KMS/IAM/TLS/native-container acceptance from Phase 4.
- Existing outbox relay, inbox/notification/webhook consumers and end-to-end timing.
  This read emits no event and does not complete broker integration.
- React and Flutter Courier screens/auth/session/networking integration remain
  Phase 7. The Should history endpoint does not replace those clients or close
  outstanding mandatory workflow deployment dependencies.

Next recommended phase: **Phase 7 — React Courier and Flutter Courier clients**,
only after an explicit instruction and fresh inspection of both client repositories
and verified Identity/API contracts. Stop after Phase 6. No frontend/mobile work,
staging, commit, push, merge or unrelated edit has been performed by this agent.
