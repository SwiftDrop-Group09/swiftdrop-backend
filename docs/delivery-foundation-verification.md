# Delivery foundation: Phase 0 assessment and Phase 1 verification

Inspected on 25 October 2026. This execution follows the Customer Fee Estimation
and Booking brief and stops at Phase 1. Earlier Identity discovery made no
repository changes. This report records the current checkout rather than
repeating the implementation claims from the historical phase reports.

## Repository assessment

All five repositories are siblings under `D:\Level03Sem01\EAD\SwiftDrop-G9`.
No AGENTS.md was found in the workspace, repository trees or ancestor paths.
Working trees were clean before feature work; no existing edits were discarded.

| Repository | Inspected baseline | Existing implementation |
| --- | --- | --- |
| swiftdrop-backend | origin/main `32604d4` | Delivery Phases 1-3 already merged: domain, EF persistence, quotes and Customer confirmation; Identity registration; Gateway routing; 11 solution projects and backend CI |
| swiftdrop-docs | main `d97fff4` | Proposed baseline 2.0 and reconciled Delivery storage/zone contracts |
| swiftdrop-infra | main `075a8b4` | External Neon configuration, explicit Identity/Delivery migration tasks, shared host/container runner and RabbitMQ |
| swiftdrop-frontend | main `c062b11` | React/TypeScript/Router scaffold; no Customer booking screens, session provider or API client |
| swiftdrop-mobile | main `e9778c1` | Flutter counter scaffold; no booking, networking or authentication infrastructure |

Backend started on local main behind the tracked remote. After fetching and
rechecking the clean tree, the feature branch
`feat/delivery-foundation-verification` was created from `origin/main` at
`32604d4`. Existing Delivery quote/confirmation work is preserved. No work is
performed on main or in other repositories.

The solution pins SDK 10.0.301 and targets net10.0, with nullable analysis and
warnings as errors. Delivery already uses EF Core/Relational/Design 10.0.9 and
Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3. No package or reference changes are
needed. Domain objects have no HTTP/EF dependencies; Infrastructure implements
Application ports. Delivery references Contracts only, never another API.

## Sources and verified requirements

All requested source documents exist and were inspected: SRS, use cases,
traceability, scope/assumptions; technical design, service catalog, data
architecture, parcel state machine and decision register; REST, API reference,
routes.json, delivery.proto and gRPC guide; event catalog and integration design;
authentication/authorization; testing strategy/cases; local setup/environment
matrix. Customer booking sequence/activity sources, the ER model, current
Compose/runner configuration and the original four-page assignment PDF were
also reviewed. Existing backend source, migrations, tests, contributor guidance,
verification scripts and CI were inspected alongside client manifests/source.

Design decisions remain **Proposed** in docs
`architecture/decisions/README.md:220`. The narrower recorded confirmations are
Neon hosting, the existing six development secret names and delivery-address
tariff selection (`architecture/decisions/README.md:239`). This execution does
not imply broader team or lecturer approval.

The existing Phase 1 schema follows these documented contracts:

- Delivery alone owns `swiftdrop_delivery`. Customer/Merchant identifiers are
  opaque references, without cross-service database access or foreign keys.
- Eight tables: delivery_zones, pricing_tiers, quotes, parcels, price_snapshots,
  booking_history, idempotency_keys and outbox_messages.
- Quotes retain authenticated owner, complete validated input JSONB, canonical
  fingerprint, tariff identity/version/currency, ten-minute expiry and single-use
  consumption. Estimation creates no parcel/history/event.
- `deliveryAddress.zoneId` selects the tariff and parcel/snapshot zone; pickup
  zone is validated separately. No cross-zone surcharge is defined. Addresses
  and recipient are retained as structured JSON text in parcel columns;
  pickup_contact remains nullable because QuoteInput supplies no sender contact.
- Pricing is baseFee + perKgFee * max(0, weightKg - 1), LKR, half-up rounding.
  Money is numeric(18,2); positive weight up to 30 kg has no extra scale limit.
  Example prices 450/60 are fixtures, not approved operational tariffs.
- New parcels start Created/version 1, with unique tracking and exclusive
  Customer or Merchant/external-order origin. A committed parcel requires one
  immutable accepted snapshot with retained tariff and delivery-zone provenance.
- Quote/tariff concurrency uses PostgreSQL xmin; parcel updates use an explicit
  Version concurrency token. Economic tariff changes require a new row/version.
- Principal/operation/key uniqueness, exact response JSONB envelope and 720-hour
  replay retention protect idempotency. Completed results and tombstones cannot
  be rewritten/deleted. Booking state, snapshot, history, quote consumption,
  idempotency and outbox share a transaction.
- StatusChanged v1 is the sole booking event. The allowlisted body contains
  IDs, status/version, timestamps and correlation data, without addresses,
  contact information or credentials. An outbox row is not broker-delivery proof.

Sources: docs `architecture/data-architecture.md:16`, `:22`, `:26`, `:28`;
`design/diagrams/er/README.md:89`, `:487`; `api/rest-api.md:28`, `:85`, `:106`;
`architecture/parcel-state-machine.md:5`; `integration/event-catalog.md:5`.

## Gaps, dependencies and contradictions

No missing Phase 1 production component or overlapping migration was found.
Existing migration `20261023142857_InitialBookingPersistence` and its model
snapshot are reused; no second migration or schema reset is justified.

The review found a misleading database test: invalid weight/origin/size were
written with UPDATE to existing parcels. The immutable-booking trigger can
raise the same SQLSTATE 23514 before the intended CHECK constraint runs. A
passing test therefore did not establish those individual constraints.
Parcel-version concurrency and mismatched snapshot-zone rejection also lacked
direct PostgreSQL regression coverage.

| Issue | Exact source and consequence |
| --- | --- |
| Runtime/migration credential separation | Docs `architecture/data-architecture.md:5` requires restricted runtime plus separate migration login. Docs `deployment/environments.md:25`, `:31` and actual infra reuse DELIVERY_DATABASE_CONNECTION_STRING for runtime/migration, without another secret. This run preserves that contract and does not claim separate least-privilege provisioning; deployment reconciliation remains outstanding. |
| Stale deployment progress prose | Docs `deployment/environments.md:19` and `deployment/docker-compose.md:39` describe Delivery adapters/migrations as future work, although backend `32604d4` and ER `design/diagrams/er/README.md:483` already contain them. No documentation-source edits are made. |
| Literal assignment containerization | Assignment PDF page 2 requires containerized database; external Neon alone does not demonstrate this. Docs `architecture/decisions/README.md:92` already records the lecturer-acceptance question. This does not prevent disposable PostgreSQL verification. |
| Broad expectedVersion wording | State-machine `:20` says all writes require expectedVersion; REST ConfirmInput at `api/rest-api.md:86` has only quoteId/acceptTotal. Preserve the precise creation contract; no request field is invented. |

Identity registration exists, but Delivery's production authentication still
fails closed through UnconfiguredIdentityHandler. Test-only principals live in
tests. Verified issuer/audience/JWKS integration remains a later phase; this run
adds no authentication bypass or duplicate Identity implementation.

Member 7's existing CreateBooking/GetBookingByExternalOrder proto is preserved.
Shared parcel/snapshot/history/event primitives and BookingAssembler already
exist; Merchant workload authorization, gRPC adapter and order reconciliation
are not implemented here. No Identity or Merchant database is accessed.

There is no outbox relay/broker-delivery verification. React and Flutter booking
integration remains later work: both clients lack the required session/network
infrastructure. Frontend pins Node 24.16.0 while installed Node is 24.11.1;
Flutter/Dart are absent from PATH. Client code is unchanged and client tests are
not part of this persistence checkpoint.

## File-level implementation and validation plan

1. Reuse all existing domain, persistence, migration, DI and application code.
   Do not add tables, migrations, endpoints, packages or another infrastructure
   stack merely to recreate completed work.
2. Change only `tests/SwiftDrop.Delivery.Tests/Persistence/PersistenceTests.cs`
   for meaningful Phase 1 database regressions: invalid INSERTs asserting exact
   CHECK names; competing parcel-version writes; snapshot/parcel zone mismatch;
   migration reapplication and data/model preservation.
3. Add this backend-local assessment/results report. Leave historical phase
   reports and the documentation repository unchanged.
4. Restore/build the existing solution; execute Delivery and full solution
   tests against private PostgreSQL; validate migration model/script, formatting,
   project boundaries/process startup and contract fixture compatibility.
5. Commit the focused tested changes locally as requested; do not push/merge or
   implement Phase 2. Preserve already merged later-phase behavior.

## Environment and database safety

The system dotnet has runtime 10.0.12 but no SDK. Exact SDK 10.0.301 was installed
outside the repository at
`C:\Users\Chamza\AppData\Local\Codex\tools\swiftdrop-dotnet-10.0.301`.
Its Microsoft archive SHA512 was verified and `dotnet --version` returned
10.0.301. Commands below prepend that directory to process PATH and DOTNET_ROOT;
no global PATH changes were made.

PostgreSQL 18.1 binaries are installed. The existing PostgreSQL service on 5432
is not used. The existing test fixture initializes its own random temporary
loopback cluster, verifies data_directory, and cleans up only its owned
directory/process. It does not create/drop databases or touch shared Neon.
No shared credentials are configured in infra .env files. Docker client exists,
but its Linux daemon is unavailable. Live Neon and container acceptance require
separate verification; no shared migration is applied by this execution.

## Observed execution results

Commands ran from the backend root with the task-local SDK on process PATH and
DOTNET_ROOT. Logging overrides disable only the Windows EventLog provider in
test processes; no production authentication configuration is changed.

| Command/check | Observed result |
| --- | --- |
| `dotnet restore SwiftDrop.sln` | Passed for all 11 projects. Two interrupted NuGet downloads retried successfully; package audit was not disabled. |
| `dotnet tool restore` | Passed; existing dotnet-ef 10.0.9 restored without manifest changes. |
| `dotnet build SwiftDrop.sln --no-restore --configuration Release` | Passed; zero warnings and zero errors. |
| `dotnet test tests/SwiftDrop.Delivery.Tests --no-build --no-restore --configuration Release --filter FullyQualifiedName~PersistenceTests --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'` | Passed 29/29 real PostgreSQL persistence cases; zero failures/skips, 14 seconds. |
| `dotnet test SwiftDrop.sln --no-build --no-restore --configuration Release --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'` | Passed 456/456: Delivery 365, Gateway 68, Identity 12, Merchant 6, Notification 5; zero failures/skips. Delivery duration 1 minute 21 seconds. Includes existing quote/booking, negative-authorization and database tests. |
| `dotnet ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build` | Passed: no changes since the existing migration. |
| `dotnet ef migrations script --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --idempotent --output src/Services/Delivery/SwiftDrop.Delivery.Api/obj/delivery-foundation-verification.sql` | Passed; generated SQL inspected. Output stays under ignored obj and is not committed. The existing migration was applied/reapplied only by the private test fixture. |
| `dotnet format SwiftDrop.sln --no-restore --include tests/SwiftDrop.Delivery.Tests/Persistence/PersistenceTests.cs --verify-no-changes --verbosity minimal` | Initially identified missing import-group spacing in the touched file; spacing corrected, rerun passed. |
| `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-foundation.ps1` | Passed: 11-project dependency/cycle audit, five real host startups/health checks, 15 missing-feature/internal/readiness probes, exact offline Identity readiness, Gateway quote/booking POST rejection with forged identity, collection GET and private Delivery listener checks. Script stopped all owned hosts. No machine execution-policy change. |
| SHA256 comparison of backend delivery.proto and StatusChanged v1 schema against docs fixtures | Both match exactly. |
| `git diff --check` | Passed. |
| `docker info --format '{{.ServerVersion}}'` | Blocked: Docker Desktop Linux engine named pipe is unavailable. No container build/start was claimed. |
| Live Neon migration/connectivity, hosted CI, React/Flutter validation | Not run. No live secrets, deployment or client changes were required for this checkpoint; hosted CI requires a pushed/reviewed branch. |

### Changes in this execution

Backend only:

- `tests/SwiftDrop.Delivery.Tests/Persistence/PersistenceTests.cs`: replaced one
  misleading UPDATE test with four invalid INSERT cases (zero weight,
  overweight, conflicting origins, unsupported size). Each asserts SQLSTATE
  23514, the exact CHECK name and absence of partial parcel/snapshot rows.
- The same file adds three tests: wrong-zone snapshot rejection with the exact
  composite FK and transaction rollback; two competing parcel-version writes
  with exactly one successful writer; repeat migration/model-drift checks that
  preserve existing zone, tariff, quote, parcel and snapshot data.
- `docs/delivery-foundation-verification.md`: this current assessment,
  file-level plan, verified results and explicit handoff/deployment limitations.

These are six additional executed persistence cases overall. The concurrency
test exercises the persistence token directly; it does not claim authorized
operational status transitions or implement assignment/cancellation handlers.

No production code, database model, migration, endpoint, DTO, package, root
configuration or other repository changed. Existing
`20261023142857_InitialBookingPersistence` remains the only Delivery migration.
No shared database was migrated/reset. No credentials or build artifacts are
included in the change.

### API and integration handoff

Existing routes are preserved, not reimplemented in this run:

- `POST /api/v1/quotes`: QuoteInput -> owner-bound ten-minute Quote, HTTP 200.
- `POST /api/v1/parcels`: `{quoteId, acceptTotal}` plus Idempotency-Key ->
  Created/version 1 parcel, tracking data, accepted price projection and Location,
  HTTP 201; identical committed retry preserves the exact original response.
- `GET /health`: process liveness only. Protected writes return 401 in the
  deployed configuration until verified Identity authentication is supplied.

The passing tests use isolated test principals and disposable PostgreSQL. They
do not establish live Identity/JWKS integration, deployed Google/local login,
broker delivery, Merchant RPC operation or end-to-end React/Flutter usability.
Quote calculation, single-use booking, immutable price/history/outbox and retry
behavior already existed in the base and remain covered by the full suite.

### Checkpoint and commits

**Implemented and tested:** the existing Phase 1 foundation and the added
regressions. **Preserved:** already merged quote/confirmation features.
**Pending external verification:** Neon permissions/connectivity, runtime versus
migration credential reconciliation, Docker build/start and hosted CI.
**Later phases:** real Identity verification, broker relay, Merchant adapter and
Customer client flows. Other service features remain out of scope.

One logical local commit is appropriate:
`test(delivery): strengthen booking persistence verification`, containing the
test change and this report. Suggested PR:
`test(delivery): verify existing Phase 1 database guarantees`, describing reused
schema, corrected constraint evidence, migration/version checks, 456 passing
tests and outstanding deployment verification. No push, merge or PR creation is
performed. Stop at Phase 1; no new Phase 2 work is started.
