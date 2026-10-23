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
be orchestrated atomically by the later application handler.

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
to safe 503 ProblemDetails; booking retry/conflict mapping remains later work.
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

These are folders within one project; dependency direction is a review convention,
not compiler-enforced project isolation.
