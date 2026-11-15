# Dispatcher initial assignment

Dispatcher and Admin may assign a Created parcel (FR-D02/TC-D02) or explicitly
reassign an Assigned parcel before pickup (FR-D04/TC-D04). Courier status/proof APIs retain their authorization.

## Assignment rules selected for this milestone

Capacity is daily: count only assignments with status Active and service_date
equal to the requested service date. Overdue and future work on other dates do
not consume this date's slots. Reassigned/Delivered/Failed/Cancelled rows do not
count. Existing workload monitoring still counts Active work across all dates;
its all-date count can exceed the configured daily capacity legitimately.

Require a provisioned active Delivery Courier profile and membership in the
parcel's delivery zone. Today additionally requires onDuty=true. Future service
dates allow an off-duty active profile. This future-duty exception was selected
by the user and needs the central proposed contract/test catalog's owner review.
There is no future roster or Identity database query/provisioning in this feature.

The server uses Operations:TimeZone (existing default Asia/Colombo). Service dates
may be today or later, with no additional scheduling horizon. Omission defaults
to business today. Service dates label scheduled work and reserve daily capacity;
they do not prohibit early pickup through existing Courier operations. Overnight
work stays attached to its original service date until closed or later reassigned.

## REST contracts

`GET /api/v1/deliveries/{parcelId}/assignment-options` is operations-only. Query
parameters are optional serviceDate=YYYY-MM-DD, page (default 1) and pageSize
(default 20, maximum 100). Invalid, unknown and duplicate parameters return 400.
A missing parcel returns 404; a parcel no longer Created returns 409.

The response contains parcelId, parcelVersion, businessToday, operationsTimeZone,
serviceDate, items, totalCount, page and pageSize. Each eligible item contains
courierId, onDuty, activeCountForServiceDate and capacity. Candidates are ordered
by courier ID ascending. Eligibility, total and page share a repeatable-read
snapshot. Empty eligibility is a successful empty page; failure is safe 503.
The preview is advisory and never reserves capacity. Courier names are absent.

`POST /api/v1/deliveries/assign` requires one Idempotency-Key (existing opaque
key rules, maximum 128 characters) and JSON parcelId, courierId, expectedVersion,
optional reassign=false and optional serviceDate. Unknown/duplicate body fields,
noninteger versions, invalid reassignment reasons, bad IDs/dates and past dates are
400. Authorization precedes validation/storage. Missing/invalid identity is 401;
other roles are 403. Missing parcel or operational courier is 404. Stale version,
invalid transition, changed-body key reuse, expired key and lock contention are
409; operation_in_progress includes Retry-After: 1. Inactive/off-duty-today,
foreign-zone and full-date couriers are 422. Storage failure is 503.

Success is HTTP 200 with the existing ParcelBookingResponse fields: id,
trackingNumber, status=Assigned, incremented version, priceSnapshot and createdAt.
Versions are exact JSON Int64 numbers. Both endpoints are private/no-store and
retain the existing activity audit middleware. Gateway already forwards their
route family; no new Gateway route is needed.

The proposed TC-D02 wording says a capacity race returns 409, whereas the REST
catalog assigns capacity rejection 422. This implementation uses 422 for the
serialized last-slot loser; stale state and contention are 409. The invariant is
one successful assignment per available daily slot. Record this difference in
central contract review rather than claiming the draft test agrees unchanged.

## Transaction and replay

Lock the parcel first and use its existing version concurrency token. Reserve an
idempotency record scoped to authenticated actor + AssignCourier + key. For a new
request, verify Created/no current assignment and lock the target Courier row
before a fresh READ COMMITTED daily count. Hold a shared lock on the matching
CourierZone row. Every allocation writer must honor the parcel-first/courier-lock
contract; reassignment coordinates both couriers in PostgreSQL UUID order.
Existing completion/failure writers only release slots and preserve parcel-first
locking. The unique Active assignment index is retained as a database guard.

Recheck date/duty/capacity after lock waits, including a business-date rollover.
Commit parcel/version, one Active assignment, history, one compatible StatusChanged
v1 outbox event and exact stored response in the same transaction. Broker delivery
is a downstream owner dependency. No event fields or private metadata are added.

Identical retained retries return the original exact body before current
state/date/eligibility checks, even after Courier pickup/failure. Fingerprinting
retains an omitted date instead of recomputing its default on retry. Changed-body
reuse conflicts. Existing 30-day retention/expiry semantics remain. A commit
acknowledgement loss is uncertain: retry the same request and key, never a new one.

## Migration and rollout dependency

20261114113953_DispatcherAssignmentIdempotency only extends
ck_idempotency_completion to permit an AssignCourier 200 stored response. The
previous constraint allowed 200 only for status/proof operations. Existing 201
booking and 200 status/proof rules, immutable receipts and schema fields remain.

The user authorized migration files and disposable-fixture testing. No shared
database application, provisioning or migration-running startup is authorized.
Apply through the canonical rollout process only after task-specific approval,
then make this API available before releasing its frontend. All existing pending
shared migrations and verified operational Courier data remain environment-owner
dependencies. Downgrade restores the old constraint and fails safely if retained
AssignCourier 200 records exist; it never deletes or rewrites them.

## Validation

AssignmentApiTests cover roles, strict validation, dates/duty, exact versions,
paging, eligibility, last-slot and same-parcel races, identical retries, expiry,
queue/manifest/operations reads, early pickup and existing failure slot release.
AssignmentReliabilityTests inject faults into parcel/assignment/history/outbox/
receipt writes and deferred commit, then retry the original key. They also
simulate a lost commit acknowledgement. AssignmentMigrationTests exercise
upgrade/down/upgrade, retained booking/status/proof envelopes, unsupported 200
rejection and safe downgrade refusal in a fixture-owned schema.

See the EAD workspace's DISPATCHER-PLAN.md for exact tested bases, worktree
fingerprints, tool versions, executed full checks and acceptance limits. Browser
fixtures and image builds do not establish shared API readiness or Docker startup.

## Reassignment eligibility preview (P4)

GET assignment-options additionally accepts reassign=true or reassign=false.
Omission means false; only exact lowercase true/false values are accepted.
Empty, duplicate, unknown, whitespace-padded or alternative boolean values return
400 validation. Reassignment mode requires Assigned and an Active assignment with
no pickup. It excludes the current courier and returns reassign=true plus
currentAssignment={courierId,serviceDate}; ordinary mode returns reassign=false
and currentAssignment=null. Existing candidate and pagination fields remain.
All context, candidates and counts share the repeatable-read snapshot.

For reassignment, omitted serviceDate preserves an existing today/future date,
or selects business today for overdue work. Top-level serviceDate is the proposed
replacement date; nested serviceDate remains the original date. Explicit past
dates remain invalid. The preview never reserves a reassignment slot.

## Atomic reassignment (P4)

POST accepts reassign=true and reason (trimmed, nonblank, 1–500 characters).
Reason is rejected unless reassign=true. Require expectedVersion, Assigned and
one Active unpicked-up assignment. Choosing its current courier returns 400
validation with errors.courierId=["Choose a different courier."]. State/version
conflicts remain 409, replacement eligibility/capacity failures 422. Omitted
POST serviceDate still defaults to business today; the UI submits its selected
date explicitly. The old courier need not remain active or eligible.

Lock parcel, then its Active assignment, then both couriers in PostgreSQL UUID
ORDER BY id FOR UPDATE order. This shares initial assignment's allocation lock
and pickup's parcel-first ownership contract. Validate eligibility and dates after
waits; flush only the old assignment's Reassigned update before adding the new
Active row. Parcel version/time, retained ownership history with normalized reason,
one Assigned-to-Assigned StatusChanged v1 event and the exact receipt commit in
the same transaction. Retirement and every later write roll back together.

Initial-assignment canonical bytes/fingerprints remain unchanged. Reassignment
fingerprints additionally include the normalized reason and mode; case/internal
whitespace remain meaningful. Changed normalized reason returns 409
idempotency_conflict. Retained exact replay precedes current date/state/version
checks, including after pickup, another reassignment or business midnight.
No extra history/event is emitted on replay. Cancellation of a client request
does not guarantee server rollback; recover an uncertain result using the same
request/key or reload authoritative state if the local attempt was lost.

The proposed state-machine narrative requests the reason in an event; strict
StatusChanged v1 forbids extra properties. The reason remains in authorized
history and is absent from broker/public tracking payloads. Contract/consumer
expectations require their owners' review; no other role's consumer is changed.
P4 requires no new schema migration; existing shared rollout restrictions remain.

P4 validation includes retirement plus parcel/replacement/history/outbox/receipt/
deferred-commit fault injection, exact lost-acknowledgement receipt replay,
reassignment-versus-pickup and same-parcel replacement races, opposite-direction
courier swaps with spare/full capacity, and initial assignment competing with
reassignment for the last slot on one date. Golden pre-P4 fingerprint vectors and
retained initial-assignment receipts remain covered. Normalized-reason replay is
verified after pickup, a later reassignment and business midnight (both omitted
and explicit dates), with changed-reason conflicts and no duplicate effects.

## Whole Dispatcher integration (P6)

`DispatcherWorkflowTests` adds four API-through-PostgreSQL cases for Dispatcher
and Admin. Only reference zones/tariffs/Courier profiles are seeded; quote and
booking, allocation, reassignment, Courier stages, failure and proof completion
use existing HTTP handlers. Proof bytes use the existing in-memory private-storage
fixture. This exercises the Delivery boundary without claiming real sign-in,
shared storage, Flutter, broker or container acceptance.

The successful-delivery path verifies Created queue/Customer ownership, assignment,
Courier manifest, every stage's operations detail/workload/active-parcel projection,
and all-date/UTC booking-day zone summaries through Delivered. The replacement
path verifies retained history, old-Courier loss of ownership and the new Courier's
Failed outcome. Both paths verify Dispatcher/Admin cannot advance Courier status
or upload proof, with unchanged persisted graphs and no proof uploads on refusal.
Post-terminal booking/assignment retries return their original responses with no
extra state, history, outbox, receipt or proof writes. Events retain strict v1
schema/private-data boundaries. Existing race/fault suites remain authoritative
for last-slot allocation, pickup races and rollback at each transaction stage.

At test commit `284ed33` (base `6b56f45`), Release restore/build passes with zero
warnings/errors and all **2,127** solution tests pass, none skipped: Delivery
1,260; Identity 409; Merchant 383; Gateway 70; Notification 5. Tests used private
disposable PostgreSQL 18.6 with external database fallbacks unset and .NET SDK
10.0.301. `DOTNET_USE_POLLING_FILE_WATCHER=1` was scoped to the check process after
the initial baseline hit the host's inotify-instance limit in Merchant test hosts.
After the user authorized a coordinated runtime switch, the full foundation
script passed at `9e5a2ba`: 13-project reference/cycle audit, five real host starts,
Gateway forwarding, internal-route isolation and fail-closed read/write probes.
The script's last private-listener output still says no gRPC methods are
implemented; that historical label establishes TCP binding only. The newer
Merchant RPC exists and remains outside this Dispatcher acceptance scope.

The implementation preserves the proposed design's service-owned transactions,
expectedVersion/current-ownership guards and outbox boundary. Contract-owner
handoffs remain: assignment-options and operations assignment-list catalog entries,
additive workload fields, future-date duty policy, TC-D02 capacity **422** versus
draft **409**, and the reassignment reason narrative versus fixed StatusChanged v1.
The reason remains private history; P6 adds no public contract/schema change.
Current Courier transition notes are optional under the updated state policy.

Shared acceptance uses two explicitly agreed test parcels and existing authorized
accounts/profiles. The user or workflow owner performs booking, allocation,
replacement and Courier actions; compare queue, manifests, counts and all-date/UTC
summary responses to independent read-only persistence. One parcel must finish
with valid proof; the other must be reassigned before pickup and fail with a
canonical reason. Keep races/fault injection in disposable fixtures.

A fresh read-only preflight on 15 November found Identity 5/5, Delivery 11/11 and
Merchant 7/7 migrations applied, no pending migrations, and nine active/on-duty
Courier profiles with zone memberships. The user then authorized canonical startup
and restoration. With backend `9e5a2ba`, frontend `6b33c39` and infra `785f0dc`,
`run.sh up --mode docker --build` passed. All seven expected containers used the
expected images; the six configured container health checks were healthy. Web
deep routes, Identity bootstrap and internal trusted JWKS returned 200. Anonymous
queue/workload/summary/options reads and assignment/status/proof writes returned
401. These anonymous refusal probes performed no authorized parcel operation.

Non-watch host mode was restored through the canonical runner; all six service
health endpoints, the workload deep page and CSRF bootstrap returned 200, and
anonymous summary returned 401. Read-only migration histories matched before
Docker, during Docker and after host restoration: Identity 5/5, Delivery 11/11,
Merchant 7/7, with no pending versions. No schema reset/downgrade, account
provisioning or shared test-parcel mutation was performed. Earlier rollout notes
above describe their original milestones.

The user explicitly deferred real user-assisted whole-workflow acceptance. P6
therefore remains pending that two-parcel run; startup/health, anonymous refusal
and fixture results do not establish real signed-in workflow acceptance. Missing
other-role workflows remain owner dependencies. No publication was performed.

## Frontend integration follow-up

Frontend normal merge `797242e` integrates current dev `17aaa30` with the unchanged
P6 tests. No backend production/test source changed. At backend `265e2dc`, frontend
`797242e` and infra `785f0dc`, fresh full foundation and canonical Docker build/start
checks pass: seven expected service images, six configured healthy container
checks, 14 HTTP/trusted-JWKS probes and anonymous operations refusal. Preflight
found the earlier stack stopped; the canonical runner finished in non-watch host
mode, with all nine host/page/bootstrap/refusal probes passing. Read-only schema
histories remain Identity 5/5, Delivery 11/11, Merchant 7/7 before/during/after,
with no pending migrations, schema reset/downgrade or shared test-parcel mutation.

The frontend's unchanged complete standard check passes all 735 tests and build
in a two-CPU-affinity child process. Its unrestricted local run retains an Identity
heading timeout; upstream dev CI and the entire Identity file separately pass.
See the frontend integration handoff for the exact environment and stability
limit. No Identity source, assertions or test settings were changed to obtain
these results. Signed-in two-parcel acceptance remains explicitly deferred.
