# Dispatcher initial assignment

This feature implements the initial assignment part of FR-D02/TC-D02. Dispatcher
and Admin may assign a Created parcel. Reassignment remains a separate milestone;
`reassign=true` is rejected. Courier status/proof APIs retain their authorization.

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
noninteger versions, unsupported reassignment, bad IDs/dates and past dates are
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
contract; future reassignment writers must coordinate both couriers consistently.
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
dates remain invalid. The preview does not enable or reserve a reassignment write.
