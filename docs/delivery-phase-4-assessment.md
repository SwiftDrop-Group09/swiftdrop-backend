# Delivery Phase 4: idempotency and transactional outbox reliability

## Current baseline and prerequisites

The actual backend was clean on main `32604d4` at the start of this execution.
The user had already merged Phases 1–3 and the editor NuGet-cache fix `60fc335`.
No merge or replay of those branches was needed. This work is isolated on
`test/delivery-booking-reliability`; main is unchanged and no push is performed.

All relevant code, tests, contribution guidance, Git status, gateway/config,
domain/mapping/migration and current documentation were reinspected before
feature edits. No AGENTS.md was found. The updated docs checkout is main
`d97fff4`; infra is main `075a8b4`. Frontend `c062b11` and mobile `e9778c1` are
unchanged. Other repositories are read-only for this phase.

The prerequisite gate was executed again before changes:

- Cached restore passed for all 11 projects, using the committed repository cache.
- Release solution build passed with zero warnings/errors.
- Full solution **450/450 passed**, zero failed/skipped: Delivery 359 (5m36s),
  Gateway 68 (2m23s), Identity 12, Merchant 6, Notification 5.
- EF reported no pending model changes; private PostgreSQL cleanup completed.
- Git remained clean throughout the prerequisite audit.

## Existing behavior retained and Phase 4 scope

Phase 3 already implemented the persistence requirements of master Tasks 4.1
and 4.2: unique principal/CreateParcel/key reservation, canonical confirmation
fingerprint, exact 201 body/status/Location replay before quote checks, 720-hour
retention, expired-key tombstones, owned quote FOR UPDATE, bounded lock waits,
atomic quote consumption and parcel/snapshot/history/outbox/result persistence.
Tracking uniqueness retries use complete rolled-back transactions. The existing
schema protects completed responses, logical events and accepted prices.

No replacement handler, in-memory deduplication, schema redesign, migration,
package upgrade, runtime credential, endpoint or production source change is
needed. Phase 4 adds stronger executable failure/concurrency evidence and the
absent relay's integration handoff. No test replaces the real booking store,
reservation SQL, quote locking or database transaction with an in-memory fake.
Test wrappers inject only the explicitly described response/acknowledgement loss.

The canonical wire contract is unchanged: POST /api/v1/parcels with quoteId,
acceptTotal and Idempotency-Key; success 201 with the six-field Parcel projection
and Location. Same key/body replays the original result, changed body returns
409 idempotency_conflict, expired retained key returns 409 key_expired, and
unresolved contention returns 409 operation_in_progress with Retry-After: 1.
Authentication remains the existing production fail-closed/test-principal seam.

## New automated evidence

`tests/SwiftDrop.Delivery.Tests/Bookings/Reliability` adds:

| Coverage | Real behavior exercised |
|---|---|
| 20 identical simultaneous confirmations | One parcel, snapshot, initial history, replay result and stable logical event; all settled requests return original response bytes/Location |
| 20 different keys on one quote | Exactly one booking/key/result/event; losing attempts cannot consume the quote again |
| Same key with different valid quote bodies | One committed winner and idempotency_conflict; the other quote stays unconsumed |
| Quote and uncommitted-key lock timeouts | Actual PostgreSQL lock waits, bounded 409/Retry-After, no partial rows, successful same-key recovery after release |
| Cancellation during observed quote lock wait | Real provider cancellation; awaited handler/transaction disposal, cleared tracked state, same-key recovery |
| Expiry while actually waiting | Clock advances only after pg_stat_activity confirms a lock wait; freshness is checked after lock acquisition |
| Lost HTTP response | Test transport discards the real successful server response; a fresh host replays stored bytes after quote expiry without another event |
| Lost COMMIT acknowledgement | Wrapper throws only after real COMMIT succeeds; initial safe 503 has one committed graph, retry returns stored 201 without quote lookup |
| Eight atomic fault points | PostgreSQL failures at reservation, parcel, snapshot, history, outbox, quote consumption, response completion and deferred COMMIT; complete rollback followed by same-key recovery |
| Event schema/privacy | Persisted Customer event and both existing domain parcel origins obey the copied StatusChanged v1 schema; private fields stay absent |
| No broker adapter | PostgreSQL-only booking succeeds and keeps one schema-valid event pending; repeated correlation headers cannot rewrite that event |

Shared test infrastructure creates a verified quote through HTTP, uses only the
existing fictional test principal, and scopes connections with a unique
application_name. Lock-sensitive tests observe actual database activity instead
of assuming a sleep establishes contention. Clock ticks use Interlocked.
Generated fault triggers/functions are scoped to the scenario and created and
removed with atomic DDL inside the fixture schema. Deferred failure proves all
SaveChanges writes completed before PostgreSQL rejects COMMIT.

The schema is copied from the existing Contracts asset into test output. Direct
assertions read every constraint used by this flat v1 schema; they are not a
general JSON Schema engine and add no validator dependency. Wire assets and
project references stay unchanged.

## Publishing handoff and limitations

No publisher, MassTransit/RabbitMQ adapter or relay exists in current backend
source. DEL/MSG individual ownership remains unallocated in current docs.
Original master Task 4.3 explicitly permits correctly formed pending outbox
rows and a documented integration contract when no relay exists. See
[the relay handoff](delivery-outbox-relay-handoff.md) for stored fields, immutable
identity/privacy, the proposed entity/bindings, acknowledgement requirements and
the remaining integration acceptance.

No competing relay, Worker/notification consumer, Merchant inbox/webhook logic,
JWT implementation, client screen or tracking/read handler is introduced.
Broker publication, consumer deduplication, crash recovery after broker ACK,
RabbitMQ delivery and notifications are not claimed. The no-adapter test proves
booking independence; it does not simulate an implemented RabbitMQ publisher.

## Current source contradictions recorded without unrelated edits

The current REST/idempotency/event wire contracts still agree with the backend.
These operational/status inconsistencies in the updated documentation remain:

- architecture/data-architecture.md:5 calls for separate runtime/migration
  logins, while deployment/environments.md's approved six-secret matrix still
  uses the same Delivery string for runtime and explicit migrations.
- Its provisioning-before-startup statement conflicts with local-setup.md's
  deliberate credential-free scaffold startup. No startup DDL is added here.
- deployment/.env.example uses older broker aliases and omits the approved
  Identity migration key; no new/renamed secrets are introduced by this phase.
- Environment/infra status text still describes Delivery handlers as absent,
  despite the actual merged implementation and passing prerequisite suite.
- The event catalog describes dedicated broker workload users, but current
  development Compose configures only broker administration and no adapters.

These do not require another booking implementation or unsafe credential
assumptions. The user-confirmed Neon, six secret names and delivery tariff-zone
choice remain in force. Other ADRs/topology/individual assignments remain
Proposed/unassigned; no lecturer/team approval is invented.

## Files and verification

Only backend files change: four new reliability test files, the test project's
schema content link, README/layer guidance, this report and the relay handoff.
Runtime code, database model/migrations and shared wire assets are unchanged.

Tests use SDK 10.0.301 and an isolated PostgreSQL 18 cluster. No shared Neon
database or broker credential was used.

| Check | Observed result |
|---|---|
| Prerequisite restore/build/test before edits | Passed; 450/450 tests; zero build warnings/errors |
| Final cached restore, all 11 projects | Passed; no package changes; online audit explicitly disabled for this offline environment |
| Final Release solution build | Passed; zero warnings and zero errors |
| Focused Phase 4 reliability suite | 20/20 passed, zero failed/skipped, 1m15s |
| Final full solution regression | 470/470 passed, zero failed/skipped: Delivery 379 (6m54s), Gateway 68 (2m23s), Identity 12, Merchant 6, Notification 5 |
| Delivery source/test formatting | Passed; no changes required |
| EF model/migration check | Passed; no pending model changes |
| Real host/Gateway smoke | Passed: all five hosts, 11-project dependency/cycle audit, 15 absent-feature probes, Identity readiness 503, protected quote/booking 401, collection GET 405, private Delivery 8081; all owned hosts stopped |
| Contract compatibility | Checked-in StatusChanged schema is copied into executable tests; schema/proto copies still match current docs and wire assets are unchanged |
| Independent reliability review | No blocking issue found; real locks/transactions/faults, cleanup and evidence limits reviewed |
| Workspace and fixture cleanup | Zero private PostgreSQL fixture directories remain; main stays at 32604d4; docs/infra/frontend/mobile remain clean at their starting commits |
| Docker | Unavailable: daemon pipe absent and local Docker config access denied; no container build/start success claimed |
| Live Neon, broker publishing, hosted CI | Not executed; provisioning, transport/relay integration and hosted execution remain external checks |

Exact final verification commands from the backend root:

```powershell
dotnet restore SwiftDrop.sln --source 'C:\Users\adith\.nuget\packages' -p:NuGetAudit=false
dotnet build SwiftDrop.sln --no-restore --configuration Release
$env:SWIFTDROP_TEST_POSTGRES_TEMP_ROOT = 'D:\VS code\swiftdrop'
dotnet test tests/SwiftDrop.Delivery.Tests/SwiftDrop.Delivery.Tests.csproj --no-build --no-restore --configuration Release --filter 'FullyQualifiedName~Bookings.Reliability' --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'
dotnet test SwiftDrop.sln --no-build --no-restore --configuration Release --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'
dotnet format SwiftDrop.sln --no-restore --include src/Services/Delivery/SwiftDrop.Delivery.Api tests/SwiftDrop.Delivery.Tests --verify-no-changes --verbosity minimal
dotnet-ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-foundation.ps1 -Configuration Release
git diff --check
docker info --format '{{.ServerVersion}}'
```

The installed EF executable was used without local-tool restore. The smoke
execution policy is process-scoped; machine/user policy is unchanged. No online
package vulnerability audit is claimed. Initial fault testing found an
order-dependent outbox fault predicate; it was corrected to use the scenario
correlation ID, independent of EF's valid insertion ordering. The corrected
20-case suite passed. No production defect was exposed.

## Checkpoint

Stop after Phase 4. The phase is committed locally as
`test(delivery): verify booking retries and outbox atomicity` after the complete
checkpoint passes. No push, merge into main or changes in other repositories.
