# Delivery booking: Phase 0 assessment and Phase 1 checkpoint

## Baseline and scope

Inspected on 23 October 2026. This execution stops at Phase 1; no quote or
booking HTTP handlers, client screens, authentication implementation, Merchant
adapter, or outbox relay are included. In the follow-up, the user confirmed Neon,
the six secret names below, and deliveryAddress.zoneId as the tariff zone.
Remaining architecture decisions stay **Proposed**, not Accepted; those specific
choices do not imply full team or lecturer approval.

| Repository | Branch / baseline | Initial working tree and progress |
|---|---|---|
| backend | main, ee95c6e | Untracked `.vscode/` preserved; Delivery health host/layer placeholders and foundation tests; Identity registration already implemented independently |
| docs | documentation, 233f707 | Clean; proposed baseline 2.0, all ADRs/DEC-01–12 Proposed |
| infra | main, 9dc82d3 | Clean; external Neon configuration, RabbitMQ, shared host/container runner; Identity-only migration task |
| frontend | main, c062b11 | Clean; React/Router/TypeScript foundation, no Customer screens/session/API client |
| mobile | main, e9778c1 | Clean; Flutter counter scaffold, no booking/network/auth/navigation infrastructure |

Backend work proceeds on `feat/delivery-booking-persistence` from the existing
backend `main`; no existing feature branch or alternative local base exists.
The follow-up authorizes local conventional commits and reconciliation in docs
and infra on their own feature branches. No push or PR is requested. Existing
frontend/mobile/Identity/Merchant code is preserved. No AGENTS.md was found.

## Sources inspected

All requested documents exist in the docs repository: requirements/srs.md,
use-case-specifications.md, requirements-traceability.md,
scope-and-assumptions.md; architecture/technical-design.md, service-catalog.md,
data-architecture.md, parcel-state-machine.md, decisions/README.md;
api/rest-api.md, api-reference.md, contracts/routes.json,
contracts/delivery.proto, grpc-services.md; integration/event-catalog.md,
integration-design.md; security/authentication.md, authorization.md;
testing/test-cases.md, test-strategy.md; deployment/local-setup.md,
environments.md. Customer booking sequence/activity sources and the ER diagrams
in design/diagrams/er/README.md exist. The original four-page assignment is
requirements/sources/ead-group-assignment.pdf; it was read, and does not specify
concrete tariff values, zone selection, or table fields.

Backend Program/project/configuration files, layer placeholders, test projects,
gateway forwarding, copied proto/event schema, README, CONTRIBUTING,
Directory.Build.props, global.json, CI and verification script were inspected.
Infra/frontend/mobile READMEs, available contributor guidance, source,
configuration, dependency manifests and scripts were inspected separately.

## Discovery baseline: existing behavior and missing work

Delivery has no domain entities, DbContext, database packages, migrations,
validators, business handlers, JWT verification, or relay. Gateway already
forwards `/api/v1/quotes` and `/api/v1/parcels` to Delivery without changing paths.
Contracts contains the same versioned delivery.proto and StatusChanged schema
as docs; these assets have no generated RPC handlers. Identity uses Npgsql and
an embedded SQL migration; Delivery has no overlapping migration to extend.

FR-C04/C05 therefore still require pricing resolution/calculation, quote and
confirmation handlers, ownership/authorization, retry orchestration, tracking
generation, frontend/mobile flows and their tests in subsequent phases. Phase 1
provides persistence primitives only, not a working booking operation.

## Verified contract rules

- REST `EstimateFee`: POST /api/v1/quotes, Customer, 200. Quote input contains
  both addresses (street/city/postalCode/zoneId), recipient name/phone and package
  weight >0 and <=30 kg, size Small/Medium/Large.
- REST `CreateParcel`: POST /api/v1/parcels, Customer owning the quote,
  quoteId/acceptTotal, Idempotency-Key, 201 with Location; future handlers must
  obtain identity from verified credentials and details from the persisted quote.
- Quotes last ten minutes, bind owner/fingerprint/pricing version, are single
  use, and create no parcel/history/event. Persisted duplicate results are checked
  before quote expiry/consumption. Foreign resources return 404.
- Formula: baseFee(zone,size,pricingVersion) + perKgFee * max(0, weightKg - 1),
  currency LKR. 450/60 are illustrative seeds, not operational tariffs. No seed
  tariffs are introduced. REST section 1 specifies half-up rounding to two
  decimals. Monetary columns use numeric(18,2), with domain guards rejecting
  unrounded input; weight uses numeric without an invented decimal-place limit.
- Confirmed parcels start Created/version 1. Snapshot, history, quote
  consumption, idempotency response and outbox belong to one transaction.
- Unique principal/operation/key plus canonical request fingerprint; exact
  response retained for 30 days; expired-key tombstones remain. Merchant
  merchantId/externalOrderId uniqueness survives key rotation.
- StatusChanged v1 has exactly the fields in the copied JSON schema: eventId,
  eventType, schemaVersion, occurredAt, parcelId, parcelVersion, merchantId,
  status, previousStatus, correlationId. No contact/address/private notes.

## Gaps, contradictions and dependencies

| Original source mismatch (docs repository unless stated) | Current resolution |
|---|---|
| ER Delivery model versus REST pricing/quote/parcel outputs | Reconciled ER tracking number, tariff version/currency, quote currency/creation/retained-tier reference, snapshot provenance, and replay expiry. Original status/body/Location are defined inside the retained JSONB response envelope. Proposed future courier/proof fields remain identified separately from Phase 1. |
| ER pickup_contact and text addresses versus structured QuoteInput | Pickup contact stays nullable. Documented exact mapping: address text preserves structured JSON street/city/postalCode/zoneId; dropoff contact preserves recipient name/phoneNumber. Complete input remains in quote JSONB. No new required sender input or DTO fields. Later handlers perform this mapping. |
| Two address zones versus one tariff/parcel zone | **Resolved by the user on 23 October 2026:** deliveryAddress.zoneId selects the tariff and is retained on parcel/snapshot; pickup zone is validated separately and retained in its address. No cross-zone surcharge is introduced. REST/data/ER/booking diagrams now state the same rule. |
| Customer booking diagrams versus canonical authorization/idempotency | Reconciled diagrams: missing/invalid credentials 401, missing role 403, missing/foreign quote 404, committed identical replay before checking quote expiry/consumption. Original 201/body/Location replay even after quote expiry. Malformed fields remain 400; unsupported service zones use canonical 422. |
| Local PostgreSQL proposal versus actual infrastructure | **Resolved by the user's Neon selection.** Canonical deployment is the infra runner/Compose with four private Neon connection strings. Superseded docs-only local PostgreSQL/bootstrap stack removed. Disposable PostgreSQL is test infrastructure only; no second shared database stack. |
| Infra host runner only forwarded Identity settings | Fixed all four owner-specific host mappings and explicit opt-in Identity/Delivery migration tasks. Normal startup performs no DDL. Delivery migration reuses DELIVERY_DATABASE_CONNECTION_STRING; no extra secret name was introduced. |

Identity owns accounts and JWT issuance. Existing registration IDs are UUIDs;
quote ownership stores a UUID without any Identity database FK/access. JWT
issuer/audience/subject serialization and concrete JWKS provisioning still need
cross-service verification in Phase 5. No fake identity or booking route is added.
Member 7 owns Merchant ingestion/recovery; documented proto operations remain
CreateBooking and GetBookingByExternalOrder. Merchant-compatible parcel origin
and external-order uniqueness are included; no RPC adapter is added. Broker
topology/relay ownership stays with Delivery/messaging integration; an outbox row
will not be represented as delivered to RabbitMQ.

## Exact implementation plan (recorded before feature code)

1. `src/Services/Delivery/SwiftDrop.Delivery.Api/Domain/`: add ParcelSize,
   ParcelStatus, DeliveryZone, PricingTier, Quote, Parcel, ParcelDetails,
   PriceSnapshot, BookingHistory, IdempotencyRecord, OutboxMessage, and common
   validation primitives. Keep EF/HTTP out of Domain. Reserve immutable data and
   exclusive Customer/Merchant parcel origin.
2. `Infrastructure/Persistence/DeliveryDbContext.cs` and
   `DeliveryEntityConfigurations.cs`: map only Delivery tables, required FKs,
   unique keys, money/weight numeric columns, JSONB, check constraints and
   optimistic concurrency. Enforce immutable snapshots and replay data.
3. `Infrastructure/Persistence/DeliveryPersistence.cs` and
   `DeliveryDbContextFactory.cs`: DI with the existing ConnectionStrings:Database
   convention, explicit migration mode, no credentials/default database fallback
   and no automatic migration during normal startup. Wire `Program.cs` and the
   Delivery csproj; add a local EF tool manifest if needed. Add
   `DeliveryDatabaseErrors.cs` to classify PostgreSQL conflicts safely for later
   application error mapping.
4. `Infrastructure/Persistence/Migrations/`: generate one initial migration and
   model snapshot; include database enforcement for accepted-price immutability.
5. `tests/SwiftDrop.Delivery.Tests/Persistence/`: disposable PostgreSQL fixture,
   domain/model and database integration tests for relationships, uniqueness,
   immutable prices, rollback and concurrent quote/idempotency writes. Preserve
   foundation tests and verify POST feature routes remain absent.
6. Delivery layer READMEs, backend README, this report: setup/migration/test
   commands, schema choices, actual observed verification and remaining work.
   No edits to the docs repository.

## Verification sequence

Restore/build after adding packages/model; generate and inspect migration;
run Delivery domain and real PostgreSQL tests; verify migrations have no pending
model changes; run solution Release build/tests, formatting checks and repository
process/project-reference script. Attempt Docker verification where available.
Do not claim a blocked command passed. Stop at Phase 1 after reporting results.

## Execution results

Phase 1 is implemented and verified against real disposable PostgreSQL 18.1.
Phase 2 has not started. No shared database was migrated or reset; the migration
was applied only to the private test cluster. The container merge check remains
blocked by the unavailable Docker daemon. The initial run made no commits;
the follow-up groups these changes into the requested conventional commits.

### Files changed (backend only)

- Domain: `DomainGuard.cs`, `ParcelSize.cs`, `ParcelStatus.cs`,
  `DeliveryZone.cs`, `PricingTier.cs`, `Quote.cs`, `ParcelDetails.cs`,
  `Parcel.cs`, `PriceSnapshot.cs`, `BookingHistory.cs`,
  `IdempotencyRecord.cs`, `OutboxMessage.cs`, and the existing Domain README,
  under `src/Services/Delivery/SwiftDrop.Delivery.Api/Domain/`.
- Persistence: `DeliveryDbContext.cs`, `DeliveryEntityConfigurations.cs`,
  `DeliveryPersistence.cs`, `DeliveryDbContextFactory.cs`,
  `DeliveryDatabaseErrors.cs`, and the three generated migration/snapshot files
  under `src/Services/Delivery/SwiftDrop.Delivery.Api/Infrastructure/Persistence/`.
- Wiring/tooling: Delivery `Program.cs` and csproj; `.config/dotnet-tools.json`
  pins EF tooling 10.0.9. EF Core/Relational/Design 10.0.9 and Npgsql EF provider
  10.0.3 are confined to Delivery; existing API project references are unchanged.
- Tests: existing `FoundationTests.cs`, new `DomainTests.cs`,
  `PersistenceConfigurationTests.cs`, `Persistence/PostgresFixture.cs`, and
  `Persistence/PersistenceTests.cs` in `tests/SwiftDrop.Delivery.Tests/`.
- Documentation: root README, Delivery Infrastructure README and this report.
  The initial run changed backend only. The follow-up also reconciles the docs
  and infra repositories as described below. Frontend/mobile and other service
  source remain unchanged. The user's untracked `.vscode/` was preserved.

### Schema and migration

Migration `20261023142857_InitialBookingPersistence` creates delivery_zones,
pricing_tiers, quotes, parcels, price_snapshots, booking_history,
idempotency_keys and outbox_messages, plus EF's migration history.
There are no users, assignments, proof, notification or Merchant tables.

Required local relationships use restrictive foreign keys. Quotes/snapshots
reference retained tariff IDs plus matching version/currency; snapshots also
match the parcel/tariff zone. Owner/actor IDs are references only, without
cross-service SQL or foreign keys. A deferred constraint trigger requires a
snapshot at parcel commit; uniqueness ensures at most one. Triggers reject
snapshot updates/deletes, quote input rewrites/reconsumption, ownership/detail
changes to a booked parcel, and tariff economic changes. Tariff activation may
change; price changes require a new row/version. This is an explicit storage
choice stricter than allowing unused-tariff edits, with no Admin API implemented.

Indexes enforce unique tracking, Merchant external order, quote consumption,
initial history, principal/operation/key and logical parcel-version/event type.
Quote concurrency uses PostgreSQL xmin; parcel version is an EF concurrency
token. Database error classification distinguishes provider uniqueness,
concurrency, constraints and dependency failures without exposing provider text.
HTTP retry/conflict mapping still belongs to the later handlers.

Replay records retain a 30-day UTC expiry (720 hours, avoiding DST arithmetic)
and a JSONB envelope containing original status/body/Location. Body is stored as
a string to preserve serialization exactly. Completed responses and tombstones
cannot be overwritten/deleted. Reservation/completion primitives exist; the
transactional booking handler and HTTP replay behavior are not implemented.

Outbox creation emits only StatusChanged v1 fields with Created, previousStatus
null, parcel version 1, stable event ID and correlation ID. PostgreSQL checks
reject missing/extra fields, JSON-null bypasses, wrong schema/type/status/date
data. Publication metadata may advance while payload and provenance remain
immutable. No broker connection or relay exists and no event delivery is claimed.

### API, Identity and Merchant handoff

Only the existing GET /health is implemented for normal Delivery startup.
POST /api/v1/quotes and POST /api/v1/parcels remain 404, as verified in tests.
`--migrate` is an explicit single-run startup mode, never implicit DDL at host
startup. Missing configuration and other service database names fail when
persistence is resolved. Liveness does not assert database readiness.

No production or test HTTP authentication handler was added in Phase 1; direct
domain/database tests operate independently of Identity. There is no anonymous
booking success, header identity, token issuer or duplicate user store. Phase 5
must provide test-only principals and real JWT verification before feature
endpoints become usable.

Merchant-compatible domain primitives are `Parcel.ForMerchant` and the shared
snapshot/history/outbox/idempotency records, with stable external-order
uniqueness. A reusable application booking handler is **not yet implemented**;
that is Phase 3 work for the later CreateBooking adapter to reuse. Member 7's
CreateBooking/GetBookingByExternalOrder fields and contracts remain unchanged.

### Initial run: commands and observed results

Commands ran from `swiftdrop-backend` unless noted. The PostgreSQL runs set
`SWIFTDROP_TEST_POSTGRES_TEMP_ROOT=D:\VS code\swiftdrop` for the sandbox's
accessible temporary parent. Fixture cleanup removed its private cluster.

| Check | Observed result |
|---|---|
| `dotnet restore SwiftDrop.sln --ignore-failed-sources` | Failed: sandbox denies NuGet network sockets; warnings-as-errors and missing sandbox-cache packages. |
| `dotnet restore SwiftDrop.sln --source 'C:\Users\adith\.nuget\packages' -p:NuGetAudit=false` | Passed for all 11 projects using the existing local package cache. This is not an online vulnerability-audit result. |
| `dotnet tool restore --add-source 'C:\Users\adith\.nuget\packages' --ignore-failed-sources` and offline-config retry | Blocked: NuGet network access denied; dotnet-ef nupkg absent from that cache. Installed global `dotnet-ef` 10.0.9 was available and used directly. |
| `dotnet-ef migrations add InitialBookingPersistence --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --output-dir Infrastructure/Persistence/Migrations` | Passed; generated migration subsequently extended with reviewable PostgreSQL integrity triggers. |
| `dotnet build SwiftDrop.sln --no-restore --configuration Release` | Passed, zero warnings/errors. |
| `dotnet test tests/SwiftDrop.Delivery.Tests --no-build --no-restore --configuration Release --logger 'console;verbosity=minimal'` | Passed 53/53, no skipped tests: 13 domain, 23 PostgreSQL cases, 7 persistence configuration, 10 host foundation. |
| `dotnet test SwiftDrop.sln --no-build --no-restore --configuration Release --logger 'console;verbosity=minimal'` | Passed 144/144: Delivery 53, Identity 12, Merchant 6, Worker 5, Gateway 68. Gateway took 2m25s while the sandbox also reported DPAPI/EventLog issues during process checks. |
| `dotnet-ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build` | Passed: no changes since the migration. |
| `dotnet-ef migrations script --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --idempotent --output src/Services/Delivery/SwiftDrop.Delivery.Api/obj/delivery-initial.sql` | Passed. SQL inspected; output stays in ignored obj, not a committed artifact. |
| `dotnet format SwiftDrop.sln --no-restore --include src/Services/Delivery/SwiftDrop.Delivery.Api tests/SwiftDrop.Delivery.Tests --verify-no-changes --verbosity minimal` | Passed. |
| `git diff --check` | Passed. |
| SHA-256 comparison of backend event schema/proto with docs fixtures | Both match the baseline exactly. |
| `./scripts/verify-foundation.ps1` | Initially blocked by local script execution policy. Retried with `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-foundation.ps1` (process scope only). |
| Process verification retry | Project-reference/cycle audit passed for 11 projects; all five hosts started and health checks passed; real Gateway forwarding to Delivery returned expected 404. Overall script failed because it expects Identity /ready =404 while existing Identity registration code returns 503 with no DB. Script and Identity were left unchanged. All started hosts were stopped. |
| `docker info --format '{{.ServerVersion}}'` | Blocked: no Docker daemon pipe; Docker config also inaccessible in sandbox. Container build/start acceptance remains outstanding. |

After the solution run, additional assertions were added to the existing three
uniqueness tests to validate safe provider-error classification. The Delivery
project was rebuilt and its 53 tests rerun; no production behavior changed.

PostgreSQL tests cover required relationships, unique identifiers, quote owner
matching and single use, exact-one immutable accepted snapshot, initial history
values, retained tariff versions, exact stored replay response, rollback after a
real database error, concurrent quote writes via xmin, concurrent idempotency
uniqueness, immutable/private-data-free event shape and publication metadata.
These are persistence tests, not claims of end-to-end booking/idempotent HTTP
behavior or broker delivery. Private PostgreSQL uses loopback trust auth only
inside test infrastructure; normal deployed hosts never register test identity.

### Follow-up: resolved contracts, Neon configuration and local commits

The six current secret inputs are exactly:

- RABBITMQ_DEFAULT_PASS
- IDENTITY_DATABASE_CONNECTION_STRING
- DELIVERY_DATABASE_CONNECTION_STRING
- MERCHANT_DATABASE_CONNECTION_STRING
- NOTIFICATIONS_DATABASE_CONNECTION_STRING
- IDENTITY_MIGRATION_CONNECTION_STRING

Names are configured as references; no real secret values were supplied,
committed, uploaded or tested against Neon. Runtime processes receive only their
own mapped ConnectionStrings:Database. Identity's explicit migration receives
ConnectionStrings:MigrationDatabase from its separate migration secret.
Delivery runtime and EF design-time factory accept the named Delivery secret
directly as a fallback; explicit ConnectionStrings:Database takes precedence.
No other service's secret can configure Delivery. RabbitMQ's default password
belongs to the broker, with restricted application credentials deferred until
messaging integration.

Infra now offers `development/run.sh migrate --mode host|docker
identity-service|delivery-service` as an explicit selected task; normal `up`
does not apply migrations. Delivery uses its listed connection secret and needs
Delivery-schema DDL rights when migrating. Host children, frontend and migration
tasks drop unrelated secret inputs. Configuration validation stays quiet so it
does not print rendered credentials. The current deployment has no live Neon
provisioning/role-grant evidence; operator credential provisioning is still needed.

The backend smoke script was corrected against existing Identity behavior:
without database configuration, Identity must return exactly 503 and the
database-not-configured ProblemDetails type. Other /ready probes remain 404.
Smoke children strip ambient database/broker secrets; Identity also gets an
empty command-line DB override so Development user secrets cannot trigger a
live connection. This verifies fail-closed readiness instead of accepting any
status. Provider-specific EventLog logging is disabled only in verification
processes to avoid sandbox permission errors.

Follow-up validation:

| Check | Observed result |
|---|---|
| Offline-cache restore and Release solution build | Passed; zero build warnings/errors. NuGet vulnerability auditing remains unavailable in this network-restricted run. |
| Full solution test command below | Passed 147/147: Delivery 56, Identity 12, Merchant 6, Worker 5, Gateway 68; no skips. |
| Corrected `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-foundation.ps1` | Passed: 11-project dependency audit, five host health checks, 16 route/readiness probes, exact Identity offline readiness assertion and private Delivery listener. Owned host processes cleaned up. |
| EF has-pending-model-changes and scoped dotnet format verification | Passed; no schema drift or formatting changes. |
| `python scripts/test-development.py` in infra | Passed 9/9 isolated tests for owning-secret scoping, exported/file precedence, CRLF-safe literal input, no startup DDL, explicit migration selection and quiet configuration validation. No live database contacted. |
| Infra canonical/root-compatible Compose configuration checks | Passed with the explicit migration profile and synthetic secret values; no container startup performed. |
| Documentation reconciliation checks | Passed: 54 Markdown files, 484 links, 57 routes, 52 requirements, 68 drafted cases, exact six secret names, compiled protobuf, event schema and GraphQL validation. All 33 diagram source/export hashes match; architecture PDF is one page and the diagram packet is 33 pages. These are documentation checks, not additional application-test or deployment claims. |

```powershell
$env:SWIFTDROP_TEST_POSTGRES_TEMP_ROOT = 'D:\VS code\swiftdrop'
dotnet test SwiftDrop.sln --no-build --no-restore --configuration Release --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'
```

Additional Delivery tests verify named-secret configuration, scoped-setting
precedence and rejection of foreign service secret configuration without opening
a connection. Resulting commit hashes are reported in the final response and
available in each repository's git log. The tested Phase 1 schema is unchanged;
no second migration, quote API, pricing handler or client screen was added.

### Remaining work and commit units

The six listed gaps are now reconciled. No operational prices were invented.
Identity issuer/audience/JWKS integration, feature handlers and tracking
generation, outbox relay, Merchant adapter, React and Flutter remain later phases.
Frontend Node is below its pinned requirement and Flutter/Dart are absent;
neither client was edited or tested in this phase.

Infra host-mode forwarding and explicit Delivery migrations are implemented.
Actual Neon credentials, deployed database privileges/connectivity, and Docker
build/start evidence still need deployment verification. No live database DDL
was performed. The smoke-script readiness mismatch has been fixed and verified.

Requested backend conventional commit groups:

1. `feat(delivery): add versioned booking domain and PostgreSQL persistence`
2. `test(delivery): verify booking persistence integrity and concurrency`
3. `docs(delivery): record phase one setup and integration dependencies`

Infra commit: `fix(infra): scope Neon secrets and add explicit delivery migrations`.
Docs commit: `docs(delivery): reconcile booking contracts and Neon deployment`.

Suggested PR: `feat(delivery): establish customer booking persistence foundation`,
describing this Phase 1 scope, resolved contract gaps and successful checks, with
Docker/Neon deployment verification still identified before merge. No push or
PR is performed. Stop here; quote API implementation requires the next phase.
