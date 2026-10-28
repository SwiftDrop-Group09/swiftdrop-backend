# Courier Phase 5: failed delivery reporting

29 October 2026. Phase 5 only: FR-R04 and TC-R04. Stop after this checkpoint;
no Courier history endpoint, clients, assignment algorithm, commit, push or merge.

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

Implementation and verification in progress.
