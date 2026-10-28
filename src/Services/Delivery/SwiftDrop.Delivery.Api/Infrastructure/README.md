# Delivery Infrastructure

Own database and external-system adapters. Implement Application ports; this service alone may access swiftdrop_delivery.

Namespace: `SwiftDrop.Delivery.Api.Infrastructure`.
Persistence uses EF Core 10/Npgsql against Neon via
`ConnectionStrings:Database`, or the named `DELIVERY_DATABASE_CONNECTION_STRING`
secret when the scoped setting is absent. The scoped setting takes precedence.
Runtime and EF design-time configuration use the same selection. Resolving persistence rejects a
database name other than `swiftdrop_delivery`; no default credentials, fallback
database, automatic host-startup migration, or Identity DB access exists.

The initial migration creates only Delivery's eight booking/configuration tables.
It includes unique tracking, Merchant external-order and principal/operation/key
constraints; pricing-version FKs; quote xmin concurrency; parcel version
concurrency; and UTC timestamps. PostgreSQL triggers enforce single-use owner
matching quote consumption, immutable booking fields/snapshots/tariffs, retained
replay results/events, and exactly one snapshot per committed parcel. The
snapshot-presence check is deferred: insert parcel and snapshot in one explicit
transaction (or one EF SaveChanges call). All related booking writes must still
be orchestrated atomically by the application handler. Customer confirmation
implements that orchestration below.

Tariff economic fields always create new rows/versions; activation may change.
Quote/snapshot references retain the original tariff ID, version and currency.
Money uses numeric(18,2), weight numeric, JSON inputs/events jsonb. The replay
JSONB envelope stores the response body as a string so PostgreSQL does not
normalize its serialized JSON; statusCode and Location are retained separately
inside that envelope. No expiry cleanup or event relay is implemented.

From the backend root, with the owning database connection provided privately:

```powershell
dotnet tool restore
dotnet ef migrations script --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --output artifacts/delivery-initial.sql
dotnet ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release
# Supply DELIVERY_DATABASE_CONNECTION_STRING privately (or ConnectionStrings__Database).
dotnet run --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-launch-profile -- --migrate
```

Review the generated SQL before applying it. Run migrations once under a
Delivery-owned login with schema DDL permissions; never run them against another
service or reset a shared database. The shared infra runner exposes the explicit
`./development/run.sh migrate --mode host delivery-service` task (or Docker mode).
It uses DELIVERY_DATABASE_CONNECTION_STRING; no additional migration secret was
introduced. That configured login needs schema DDL rights for the task. Neon
role provisioning/grants still require deployment verification. No connection secret is
required to generate or inspect a migration script.

PostgreSQL integration tests create a private cluster on a random loopback port
using installed initdb/postgres/pg_ctl. They fail explicitly if unavailable.
`SWIFTDROP_TEST_POSTGRES_BIN` selects a bin directory; set
`SWIFTDROP_TEST_POSTGRES_TEMP_ROOT` to an accessible temporary parent if needed.
Alternatively, explicitly configure `SWIFTDROP_TEST_DELIVERY_DATABASE` for an
isolated test database: tests create/drop only a generated temporary schema,
never the database. Never supply a production database. No Docker stack, shared
service mutation, fake auth, or credentials are needed for the private cluster.

```powershell
$env:SWIFTDROP_TEST_POSTGRES_TEMP_ROOT = (Get-Location).Path
dotnet test tests/SwiftDrop.Delivery.Tests --configuration Release
```

`DeliveryDatabaseErrors` classifies provider conflicts/concurrency/dependency
failures without exposing SQL messages. Phase 2 maps quote dependency failures
to safe 503 ProblemDetails; Customer booking retry/conflict mapping is implemented
by the confirmation store/handler below.
No publish is claimed for a persisted outbox record. A future relay may
update published_at after broker confirmation, retaining stable payload/event ID;
deletion remains disabled until a provenance-retention policy is implemented.

Phase 2 `Quotes/PostgresQuoteStore` reads both active zones and at most two active
destination-zone/size tariffs in a repeatable-read configuration snapshot. The
application requires exactly one tariff, then persists one owner-bound quote
using its immutable retained tariff reference. No default tariff is seeded or
chosen from conflicting versions. Quote persistence adds no parcel, snapshot,
history, idempotency or outbox record and requires no schema change.

`Authentication/ClaimsCurrentCustomer` consumes trusted authenticated claims.
The registered `UnconfiguredIdentityHandler` always declines authentication;
caller headers and unverified bearer strings never create a Customer identity.
Replace this with real Identity token verification in the authentication phase.
Only the test project registers the fixed-principal authentication handler used
by HTTP/database tests; no environment switch enables it in deployed hosts.

Phase 3 `Bookings/PostgresBookingStore` owns the confirmation transaction. A
parameterized INSERT with ON CONFLICT reserves the principal/CreateParcel/key
scope at READ COMMITTED isolation. A conflicting committed key is read for exact
replay. An owner-filtered FOR UPDATE locks the quote before freshness checks;
different keys cannot consume the same quote twice. A transaction-local two-second
lock timeout maps prolonged contention to operation_in_progress with Retry-After.

The handler saves parcel, immutable snapshot, initial history, pending
StatusChanged v1 outbox, quote consumption and completed replay response before
commit. Invalid confirmations and failed writes roll back the reservation and
all booking changes. Tracking uniqueness collisions retry the whole transaction
up to three attempts with a fresh cryptographic 128-bit code. No broker call,
relay, schema change or cross-service database access is introduced. A
connection failure during commit can leave the outcome unknown to the caller;
retrying the same key resolves the committed result safely.

Creation timestamps are truncated to PostgreSQL microseconds before constructing
the response, history and event, keeping occurredAt consistent with timestamptz.
Stored quote tariffs are honored even after activation changes. Parcel addresses
and recipient contact are structured JSON text from the retained quote, with
pickup_contact null because no pickup contact is supplied by QuoteInput.

Phase 4 adds real PostgreSQL reliability evidence for 20-request contention,
lock timeout/cancellation, response/commit acknowledgement loss and rollback
through deferred COMMIT failure. Event tests read the checked-in schema and
verify private fields and stable identity. No publisher or broker adapter is
registered; pending rows await the [relay handoff](../../../../../docs/delivery-outbox-relay-handoff.md).
An existing outbox row is not evidence of publication or consumer effects.

Phase 7 extracts `Bookings/BookingWritePersistence.SaveAsync` for Customer and
future authorized Merchant adapters. It requires an existing EF transaction
before tracking rows and saves the common parcel/snapshot/history/outbox set,
including pending changes in the same booking-scoped context. It never commits,
reserves a key, authenticates, consumes a quote or publishes. Customer store
retains its tracked-quote/reservation guard and transaction/error handling.
Merchant reservation/fingerprint/result persistence remains the integration
owner's responsibility; the existing unique external-order index alone is not
a complete replay implementation. See the
[Member 7 handoff](../../../../../docs/delivery-merchant-booking-handoff.md).

These are folders within one project; dependency direction is a review convention,
not compiler-enforced project isolation.

## Courier persistence foundation (Phase 1)

`20261028110532_CourierPersistenceFoundation` adds `couriers`, `courier_zones`
and `proof_metadata`. Courier IDs retain the separately verified Identity UUID;
no account credentials or cross-database FK are introduced. Capacity and version
are positive, and Courier.Version is an EF concurrency token. Zone memberships
have a composite primary key and restrictive local Courier/DeliveryZone FKs.
No profile is seeded or provisioned by the API.

Proof metadata stores Photo/Signature, parcel FK, opaque private storage reference,
recipient name, actor UUID and UTC upload time. Object references are unique and
URLs are rejected. The actor is an audit UUID reference, not an Identity SQL FK.
Metadata is not proof of an actual upload: no storage adapter, upload verification,
assignment check, completion handler or public proof DTO is enabled here.
Future upload code must confirm private storage before committing metadata and
the authorized status/history/outbox transaction; staging and orphan cleanup
remain Phase 4 work. Do not serialize persistence entities as public responses.

The existing `booking_history` is now append-only through both EF SaveChanges
and a PostgreSQL UPDATE/DELETE rejection trigger. Its schema/factory and all
existing Booking writes are reused. The new migration leaves the initial
migration, Parcel.Version, quote/snapshot/replay rules and event contracts intact.
Normal startup still performs no DDL. Generate/review migration scripts with the
existing commands above; do not apply them to a shared database without the
team's deployment process.

The user confirmed Active/Reassigned/Delivered/Failed/Cancelled assignment
statuses and at least one photo OR signature (both accepted) for final proof.
`20261028112000_AssignmentsPersistence` adds `assignments`, with restrictive
parcel/Courier FKs, service-date/manifest/history indexes, timestamp/lifecycle
checks and PostgreSQL xmin concurrency. `ux_assignments_current_parcel` uniquely
indexes parcel_id WHERE status = 'Active'. Always use that predicate, never
the most recent assignment timestamp, to determine current ownership.

Assignments retain their original Courier, parcel, service date and assigned
time. A SQL trigger blocks deletion, ownership/schedule rewrites, resurrection
of a closed row and changes to an already recorded pickup time. Reassignment
and cancellation are before pickup only; terminal assignments release the
Active slot and remain available for historical ownership. No new closure-time
field was invented: authoritative terminal timestamps will remain in parcel
status history. Rescheduling is represented by the replacement assignment.

The domain operations mutate only assignment records; they do not authorize a
caller or advance a parcel. Future application handlers must lock/conditionally
write the parcel version and current assignment, enforce Courier/zone/capacity
rules, and persist state/history/outbox in one transaction. For replacement,
flush the old row's Reassigned state before inserting the new Active row, inside
that same transaction; EF insert/update ordering alone is not sufficient for a
filtered unique index. A failed replacement must roll back the release. Actual
dispatcher allocation and status/proof endpoints remain later work.

Proof metadata supports photo alone, signature alone and both; the selected
policy must be enforced by the future authorized proof-completion handler after
private object persistence. Phase 1 supplies no successful completion endpoint.
See the
[assessment and actual test results](../../../../../docs/courier-phase-1-assessment.md).
Phase 2 manifest/detail handlers and React/Flutter Courier work have not started.
