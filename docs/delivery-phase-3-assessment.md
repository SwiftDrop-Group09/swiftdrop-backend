# Delivery Phase 3: explicit Customer booking confirmation

## Actual baseline and prerequisite gate

This execution implements Phase 3 / FR-C04 only. The current backend was
reinspected on `feat/delivery-fee-quotes` at `2a7a1ab`, with the complete Phase 2
work still uncommitted and the preexisting untracked `.vscode/` untouched.
There was a protected quote endpoint and reliable PostgreSQL foundation, but
no parcel confirmation endpoint or atomic booking application handler.

Before any feature edits, cached restore, Release solution build and all
**339/339 prerequisite tests** passed (Delivery 248, Gateway 68, Identity 12,
Merchant 6, Notification 5). Build had zero warnings/errors; EF reported no
pending model changes. Disposable PostgreSQL cleanup completed.

The tested Phase 2 work was captured separately as `ede48d9`
(`feat(delivery): add validated owner-bound fee quotes`). The new branch
`feat/delivery-booking-confirmation` starts from that checkpoint. No merge into
main is needed; main and all other repositories remain unchanged. Commits stay
local; no push is authorized by this request.

Current documentation is `swiftdrop-docs` at `2ea9695`, branch
`feature/docs-reconcile-delivery-neon`; infrastructure is at `851d946`. The
canonical REST, GraphQL projection, use-case, security, state, data and event
contracts were read alongside the actual domain, migration, tests and gateway.
Design decisions remain Proposed except the user's explicit Neon, secret-name
and delivery-address tariff-zone choices. No AGENTS.md was found.

## Implemented contract and confirmation rules

`POST /api/v1/parcels`, operation `CreateParcel`, requires a verified Customer
principal and exactly one nonblank opaque `Idempotency-Key` of at most 128
characters without control characters. Keys are preserved exactly. Only
`quoteId` and numeric `acceptTotal` are accepted in the JSON body. UUID parsing,
nonnegative two-decimal money/storage range and exact decimal representation
are validated server-side. Unknown fields, duplicate JSON properties, numeric
strings, malformed JSON and replacement ownership/address/contact inputs are
rejected before storage.

Owner identity comes only from `ICurrentCustomer`. Authentication and role
checks precede parsing/storage. Missing or foreign quotes return the same 404.
New confirmations check expiry at the instant the owned quote lock is acquired,
consumption state and accepted total. Retained JSONB input is strictly parsed,
normalized and serialized again before checking its complete owner/tariff-bound
fingerprint; raw JSONB property order is never used as the canonical input.
Retained tier ID/version/currency, delivery zone, package size and exact
calculated components/total must agree with the quote.

The handler honors a still-valid quote's retained tariff even after activation
changes or repricing. It does not silently substitute current prices or invent
another geographic eligibility rule during confirmation. Both structured
addresses and recipient details come from the quote. Parcel zone and snapshot
zone use deliveryAddress.zoneId; pickup zone stays in the pickup JSON. The
pickup contact is null because the input supplies no sender contact.

Success returns 201 and Location `/api/v1/parcels/{id}` with exactly:

- id, trackingNumber, status `Created`, version `1`, priceSnapshot, createdAt.
- priceSnapshot contains total, currency `LKR`, pricingVersion, matching the
  existing GraphQL/REST minimal projection.

The Location uses the canonical GetParcel path. GetParcel, parcel listing,
public tracking and other read handlers are outside this phase. Collection GET
therefore returns 405 at the new POST-only path; an individual GET remains
unimplemented. No successful placeholder read endpoint is added.

Tracking uses 16 cryptographically random bytes (128 random bits), formatted
`TRK-` plus 32 hexadecimal digits. The documentation requires randomness rather
than a fixed formatting regex; database uniqueness remains authoritative.
Tracking uniqueness collisions retry the complete transaction up to three times
with a new code. Creation timestamps are normalized to UTC microseconds before
building the response, snapshot, history and event, avoiding a mismatch between
PostgreSQL timestamptz and JSON event timestamps.

## Atomicity, replay and reusable booking records

`PostgresBookingStore` starts a READ COMMITTED transaction and uses a
parameterized INSERT ON CONFLICT to reserve the existing unique
principal/CreateParcel/key scope. A conflict waits for the winner and then reads
its committed result. A transaction-local two-second lock timeout bounds waits
on a key or quote. New reservations remain uncommitted until booking succeeds.
The owned quote is selected FOR UPDATE; xmin supplies an additional EF guard.
Different keys cannot create two parcels from the same quote.

One successful transaction persists all of:

1. Created Customer parcel, version 1, and its unique tracking number.
2. Immutable accepted price snapshot with retained tariff provenance.
3. Initial history with null previous status and the verified Customer actor.
4. One pending StatusChanged v1 outbox row with Created/version 1, previousStatus
   null, merchantId null, stable event ID and request correlation ID.
5. Quote consumption by that parcel.
6. The completed idempotency record containing the exact serialized 201 body,
   status and Location, retained for 30 days.

Invalid confirmation or failed writes dispose/roll back the transaction,
including the new reservation. An identical committed request is resolved
before quote lookup/expiry/consumption and returns the original status/body and
Location. The canonical confirmation fingerprint normalizes the quote UUID and
decimal value, scoped by the verified principal and operation. Changed payloads
conflict; expired retained keys remain tombstones. No in-memory replay map is
used. A connection failure during commit can leave the caller uncertain about
the outcome; retrying the same key resolves any committed result.

`BookingAssembler` builds snapshot/history/outbox records from an already
authorized domain parcel and verified retained pricing. It provides the common
record construction for a later Merchant adapter without combining Customer
and service authentication. No Merchant gRPC endpoint, inbox/reconciliation
workflow, API-key implementation or cross-service DB access is introduced.

The necessary replay and outbox writes belong here because original Phase 3
tasks 3.2, 3.4 and 3.6 explicitly require idempotency state, an atomic replay
result/outbox and safe conflicts/concurrency. Phase 4's broader reliability
campaign and publishing integration remain later work. No RabbitMQ call occurs
inside booking; an outbox row is not evidence of event delivery.

## Errors and external dependencies

ProblemDetails includes stable type/title/status/code/correlationId and optional
safe field errors. Parser/provider messages, credentials, addresses/contact
details and stack traces are excluded. Correlation IDs use the existing bounded
middleware and are carried into the event.

| Status | Codes / conditions |
|---|---|
| 400 | validation: malformed or unsupported input/key |
| 401 | unauthorized: missing verified subject/authentication |
| 403 | forbidden: authenticated identity lacks Customer role |
| 404 | not_found: missing or foreign quote |
| 409 | quote_expired, quote_consumed, price_mismatch, quote_conflict, idempotency_conflict, key_expired |
| 409 | operation_in_progress for unresolved contention; Retry-After: 1 |
| 503 | delivery_unavailable for configuration/storage failure or exhausted tracking collision retries |

Production authentication remains deliberately closed until Identity provides
verified token integration. Positive HTTP tests register fictional principals
only in test infrastructure; caller identity headers/arbitrary bearer tokens
remain unauthorized. Identity code/database, authentication issuance, Neon
provisioning and operational prices are not changed. The same six previously
selected secret names remain in force; no credential is committed.

## Exact file groups

All edits are inside swiftdrop-backend:

- Application/Bookings: Confirm/result DTOs, validator/canonical confirmation,
  transaction port, tracking port, quote materializer, response codec, shared
  record assembler and CustomerBookingHandler.
- Application/Serialization/ExactDecimalConverter.cs: moved from Presentation
  without changing numeric behavior; quote and booking parsing share it.
- Infrastructure/Bookings: PostgreSQL transaction adapter and cryptographic
  tracking generator. Existing context, model and migration remain unchanged.
- Presentation/BookingEndpoints.cs and BookingProblems.cs; Program.cs DI/map;
  QuoteEndpoints.cs only updates the converter namespace.
- Tests/Bookings: application and real PostgreSQL/HTTP cases; FoundationTests
  and one obsolete quote test now reflect the protected confirmation route;
  ExactDecimalTests updates its converter namespace.
- scripts/verify-foundation.ps1 verifies both protected POSTs through Gateway
  and the POST-only collection method behavior, retaining service-boundary,
  readiness and private-listener checks.
- README/layer guides, ParcelDetails comment and this checkpoint report.

There are no new packages, project references, migrations, schema changes,
database resets or edits to Identity/Merchant/Worker/Gateway source, shared wire
assets, infra, docs repository, clients or preexisting .vscode settings.

## Verification results

Checks ran with SDK 10.0.301 and installed PostgreSQL 18 binaries. Database
tests used a private loopback cluster, not a shared Neon database. No broker or
live service credential was required.

| Check | Observed result |
|---|---|
| Prerequisite restore/build/test gate before edits | Passed; 339/339 tests; build zero warnings/errors |
| Final cached solution restore | Passed, all 11 projects up to date; online package audit explicitly disabled in the restricted offline environment |
| Final Release solution build | Passed, zero warnings and zero errors |
| First atomic booking HTTP/PostgreSQL case | 1/1 passed, 5 seconds |
| Initial booking HTTP/PostgreSQL matrix | 50/50 passed, zero failed/skipped, 2m9s; the expanded matrix is included in the final suite |
| Final booking application tests | 58/58 passed, zero failed/skipped, 135ms |
| Final full solution regression | 450/450 passed, zero failed/skipped: Delivery 359 (5m31s), Gateway 68 (2m22s), Identity 12, Merchant 6, Notification 5 |
| Scoped Delivery source/test formatting | Passed; no changes required |
| EF migration/model comparison | Passed; no pending model changes |
| Real-process Gateway/foundation smoke | Passed: 11-project boundary/cycle audit, all five hosts, 15 missing-feature probes, Identity readiness 503, both protected POSTs 401, POST-only collection GET 405, private Delivery 8081; all owned hosts stopped |
| Contract asset comparison | delivery.proto and StatusChanged v1 schema match current docs byte-for-byte; neither changed |
| Security/contract review | No blocking issue found; test-only principals, safe errors, retained-price/input checks, atomic reservation and quote lock verified |
| Git/workspace and fixture cleanup | Passed; scoped staged diff reviewed, preexisting .vscode excluded, other repositories clean/unchanged, private PostgreSQL fixture disposal passed and temporary directory removed |
| Docker | Unavailable: daemon pipe absent and local Docker config access denied; no Docker build/start success claimed |
| Live Neon, real Identity tokens, hosted CI | Not executed; shared provisioning, real Identity verification and hosted CI evidence remain external checks |

The final suite includes 53 booking HTTP/PostgreSQL cases and 58 booking
application cases, adding 111 tests to the verified prerequisite baseline.

Exact executed commands from the backend root:

```powershell
dotnet restore SwiftDrop.sln --source 'C:\Users\adith\.nuget\packages' -p:NuGetAudit=false
dotnet build SwiftDrop.sln --no-restore --configuration Release
$env:SWIFTDROP_TEST_POSTGRES_TEMP_ROOT = 'D:\VS code\swiftdrop'
dotnet test tests/SwiftDrop.Delivery.Tests/SwiftDrop.Delivery.Tests.csproj --no-build --no-restore --configuration Release --filter 'FullyQualifiedName~ConfirmedQuoteAtomicallyCreatesTheExactBookingAndPrivateDataFreeEvent' --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'
dotnet test tests/SwiftDrop.Delivery.Tests/SwiftDrop.Delivery.Tests.csproj --no-build --no-restore --configuration Release --filter 'FullyQualifiedName~BookingApiTests' --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'
dotnet test tests/SwiftDrop.Delivery.Tests/SwiftDrop.Delivery.Tests.csproj --no-build --no-restore --configuration Release --filter 'FullyQualifiedName~BookingApplicationTests' --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'
dotnet test SwiftDrop.sln --no-build --no-restore --configuration Release --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'
dotnet format SwiftDrop.sln --no-restore --include src/Services/Delivery/SwiftDrop.Delivery.Api tests/SwiftDrop.Delivery.Tests --verify-no-changes --verbosity minimal
dotnet-ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-foundation.ps1 -Configuration Release
git diff --check
docker info --format '{{.ServerVersion}}'
```

The installed dotnet-ef executable was used for the model comparison;
local-tool package restore was not performed. The smoke invocation sets
execution policy for that process only; machine/user policy is unchanged. No
online vulnerability audit, container verification, live Neon migration or
broker delivery is implied.

## Checkpoint

Stop after Phase 3. No Phase 4 relay/broker work, real Identity verification,
Merchant RPC, tracking/read API or client screens are implemented or claimed.
The reviewed phase is committed locally as
`feat(delivery): confirm customer quotes atomically`, including its tests and
checkpoint documentation. The preexisting .vscode work remains untracked.
