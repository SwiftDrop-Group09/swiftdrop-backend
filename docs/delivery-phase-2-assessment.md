# Delivery Phase 2: fee estimation and persisted quotes

## Scope, baseline and prerequisite evidence

Only FR-C05 / Phase 2 is implemented here. Backend branch
`feat/delivery-fee-quotes` was created from the actual Phase 1 branch at
`2a7a1ab`; the feature, tests and documentation commits were already present.
No merge was needed. No commit, push or merge is performed for this phase.
The preexisting untracked `.vscode/` is preserved. Other repositories are read
only; their files and branches are not changed by this phase.

Before feature edits, the current Program, project/package references, layer
code, entities/mappings/migration, tests, README/contributor guidance, gateway
routing and current REST/data/security/use-case/test contracts were reinspected.
There was no quote handler, pricing calculator, authentication seam or HTTP
quote endpoint. Current local docs are on `feature/docs-reconcile-delivery-neon`
at `2ea9695`; infra has the previously committed Neon configuration. Those
choices were verified rather than assumed from an earlier plan. No AGENTS.md
files were found in the workspace.

Prerequisites were executed again before implementation:

- Offline-cache restore of all 11 projects passed.
- Release solution build passed with zero warnings/errors.
- Delivery tests passed **56/56**, including real isolated PostgreSQL tests.
- EF reported no pending model changes. Test cluster cleanup was verified.

The design baseline remains Proposed, except the user's explicit Neon, secret
name and delivery-address tariff-zone choices. No team/lecturer approval is
implied. Original assignment/Phase 0 findings remain in the Phase 1 report.

## Implemented request and response

`POST /api/v1/quotes`, operation name `EstimateFee`, returns HTTP **200** after
authenticated Customer validation and successful persistence. The Gateway route
already belonged to Delivery and is unchanged. No route aliases were added.

Input contains exactly:

- `pickupAddress` and `deliveryAddress`: street, city, postalCode, zoneId.
- `packageDetails`: numeric weightKg greater than zero and at most 30, and size
  exactly Small, Medium or Large.
- `recipient`: name and phoneNumber.

Required strings must be nonblank and persistable in PostgreSQL JSONB. Surrounding
whitespace is normalized. Both zone IDs must be nonempty UUIDs and reference
active Delivery zones. Address/name/phone lengths and national formats are not
documented, so no arbitrary business limits or postal/phone regex was added.
Unknown fields at every input level, duplicate JSON properties, malformed JSON,
wrong JSON types and numeric strings are rejected. Ownership/pricing inputs such
as userId, merchantId, currency, pricingVersion or a supplied total are rejected.
Embedded null characters are rejected before the database is used.

JSON weight conversion requires an exactly representable C# decimal: excess
digits cannot silently round an amount above 30 into the valid range or change a
rounding midpoint/fingerprint. Equivalent exponents and insignificant trailing
zeros remain valid; there is no invented three-decimal weight rule. Values that
underflow/overflow decimal or require silent rounding return a safe 400.

The response has exactly `quoteId`, `total`, `currency`, `pricingVersion`,
`expiresAt`, and `fingerprint`. No owner/address/contact information, tracking
number, parcel ID or extra breakdown is returned by this contract.

Errors use safe ProblemDetails with type, title, status, code, correlationId and
optional field errors. X-Correlation-ID is generated if absent; one bounded
printable ASCII incoming identifier may be retained. It is never a credential.

| Status | Behavior |
|---|---|
| 400 | Malformed/unknown/invalid fields; code validation; no database use needed |
| 401 | Missing verified Customer subject/authentication; code unauthorized |
| 403 | Authenticated principal lacks Customer role; code forbidden |
| 422 | Pickup or delivery zone missing/inactive; code unsupported_zone |
| 503 | No unambiguous active tariff, or unrepresentable tariff total: pricing_unavailable; storage/configuration failure: delivery_unavailable |

Provider messages, connection strings, private input, stack traces and parser
exception text are excluded from responses. Authorizing the principal precedes
body parsing; application authorization and validation also precede storage.

## Pricing, canonical input and persistence

The pure decimal calculator uses:

`baseRate + perKgRate * max(0, weightKg - 1)`

Currency is LKR, rounded half-up to two decimals. The tariff is selected using
`deliveryAddress.zoneId`, size and its retained pricing version. Pickup zone
eligibility is validated separately and creates no cross-zone surcharge. Fees
and components must fit the Phase 1 numeric(18,2) storage range. The 450/60/2.5
example is seeded only by tests; no operational prices were invented.

Inputs and returned money use C# decimal. Internally, bounded decimal
coefficients and integer cents preserve the exact product before one half-up
rounding step; this prevents intermediate decimal multiplication from moving a
valid weight across a half-cent boundary. No weight-precision business rule is
introduced.

The complete normalized input is serialized in a deterministic property order,
with canonical UUIDs and invariant decimal representation. SHA-256 binds that
JSON to the verified owner and retained tariff ID/version/currency. Request
property order, insignificant decimal zeros and surrounding text whitespace
cannot arbitrarily change the fingerprint; changing owner, recipient, address
or tariff version does. The fingerprint contains no plaintext private fields.

The existing Quote entity stores owner, full input JSONB, fingerprint, total,
currency, retained tariff/version, UTC creation time and expiry ten minutes
later. Its consumption reference stays null. Historical quotes and retained
tariff prices are not rewritten when activation changes. A new estimate after
an earlier quote expires creates a new ten-minute quote; booking confirmation
and expiry rejection belong to Phase 3.

The store reads eligibility/tariffs in a repeatable-read configuration snapshot.
Only the quote insert is persisted. Tests compare database counts and verify
that parcels, price snapshots, booking history, idempotency and outbox rows do
not change. Declining a preview requires no mutation or new endpoint. A real
PostgreSQL quote INSERT rejection is tested to verify safe failure/no row.

The Phase 1 schema, constraints and migration are reused unchanged. No new
migration, schema reset, shared Neon operation or broker call was introduced.

## Authentication and unresolved configuration dependencies

Identity currently has registration/readiness only: no login/JWKS or actual
token-verification integration exists. Phase 2 introduces `ICurrentCustomer`
consuming an authenticated principal without HTTP/header dependencies in the
application. The normal authentication handler always returns no result and
protected requests return 401. It never treats X-User-* headers or arbitrary
bearer tokens as proof of identity. Missing database configuration does not turn
these requests into anonymous success or an exception.

Positive HTTP tests replace authentication only inside the test host, using a
fixed fictional principal and pinned clock. No deployed Development/Production
setting enables that handler. A verified UUID sub and Customer role are required;
different Customers receive their own separately bound quotes. Real issuer,
audience, RS256/JWKS and cross-service integration remain the Identity phase's
dependency. No second account/token implementation is added to Delivery.

Two existing configuration gaps were handled conservatively:

- Phase 1 allows multiple active versions with no ordering/effective-date rule.
  The application requires exactly one active tariff for zone/size. Zero or
  multiple matches return safe 503; no lexical latest-version policy or new
  active-version schema constraint is invented.
- Future ZoneInput specifies postal membership, but current optional boundaries
  JSON has no defined shape. Phase 2 validates explicit active zone IDs and
  required address fields. Automatic postal-to-zone/geographic lookup remains
  unimplemented until its configuration contract is defined. No guessed JSON
  boundary structure or new Admin workflow is added.

These are documented limits, not claims that production pricing/Identity
configuration or geographic coverage has been provisioned.

## Files changed

All changes are in swiftdrop-backend:

- Application: `ICurrentCustomer.cs`; Quotes/ DTOs, validator, normalized
  canonical serialization, calculator, store port and QuoteEstimator.
- Infrastructure: Authentication/ trusted-principal adapter and closed handler;
  Quotes/PostgresQuoteStore.cs, using the existing Delivery context/config.
- Presentation: strict quote endpoint/decimal converter and safe ProblemDetails.
- Program.cs: DI, clock, authentication/authorization, correlation/error middleware
  and quote endpoint mapping. No new package or project reference.
- Delivery tests: Quotes/ application, exact-decimal and PostgreSQL/HTTP tests;
  fixed-principal test host; FoundationTests now expects protected quote POST401
  while parcel POST remains404.
- scripts/verify-foundation.ps1: real Gateway quote POST rejects supplied fake
  identity with401; existing service/project-boundary/readiness checks retained.
- README/layer guides and this report. No change to docs/infra/clients/Identity/
  Merchant/Worker/Gateway sources or shared wire contract assets.

## Verification results

Commands ran from the backend root with SDK 10.0.301. PostgreSQL tests used
installed PostgreSQL 18 binaries and a generated private loopback cluster. No
shared/test Neon credential or broker was required.

| Check | Observed result |
|---|---|
| Offline-cache restore, all 11 projects | Passed; package audit was explicitly disabled for the restricted offline environment |
| Final Release solution build | Passed; zero warnings and zero errors |
| New exact-product unit + HTTP/PostgreSQL regressions | 14/14 passed, zero failed/skipped, 11 seconds |
| Final full solution regression | 339/339 passed, zero failed/skipped: Delivery 248 (3m13s), Gateway 68 (2m23s), Identity 12, Merchant 6, Notification 5 |
| Scoped Delivery source/test formatting | Passed; no changes required |
| EF migration/model comparison | Passed; no pending model changes |
| Real-process foundation/Gateway smoke | Passed: 11-project boundary/cycle audit, all five hosts, 16 existing probes, Identity readiness 503, protected quote 401 and private Delivery 8081; all owned hosts stopped |
| Shared wire contract assets | delivery.proto and StatusChanged v1 schema match the current docs byte-for-byte; no asset changes |
| Git whitespace, cleanup and workspace checks | Passed; private PostgreSQL fixture disposal completed and its temporary directory was removed; no staged changes, other repositories clean/unchanged, preexisting .vscode preserved |
| Docker validation | Unavailable: Docker daemon pipe is absent; local Docker config also denies access. No container startup/build success is claimed |
| Live Neon / hosted CI / real Identity token verification | Not executed; credentials/verified Identity integration/hosted run are not available in this execution |

Executed commands:

```powershell
dotnet restore SwiftDrop.sln --source 'C:\Users\adith\.nuget\packages' -p:NuGetAudit=false
dotnet build SwiftDrop.sln --no-restore --configuration Release
$env:SWIFTDROP_TEST_POSTGRES_TEMP_ROOT = 'D:\VS code\swiftdrop'
dotnet test tests/SwiftDrop.Delivery.Tests/SwiftDrop.Delivery.Tests.csproj --no-build --no-restore --configuration Release --filter 'FullyQualifiedName~PriceRoundsTheExactProductOnceAcrossHalfCentBoundaries|FullyQualifiedName~RepresentableWeightKeepsExactHalfCentBoundaryThroughPersistence' --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'
dotnet test SwiftDrop.sln --no-build --no-restore --configuration Release --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'
dotnet format SwiftDrop.sln --no-restore --include src/Services/Delivery/SwiftDrop.Delivery.Api tests/SwiftDrop.Delivery.Tests --verify-no-changes --verbosity minimal
dotnet-ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-foundation.ps1 -Configuration Release
git diff --check
docker info --format '{{.ServerVersion}}'
```

Direct script invocation was blocked by this shell's execution policy. The smoke
check passed using the process-scoped invocation above; machine/user policy was
not changed. The installed dotnet-ef executable was used for the offline model
comparison. Online package vulnerability audit and local-tool package restore
are not claimed.

Intermediate test runs passed 183 then 234 Delivery tests, and a full solution
run passed 325 tests before the final exact-product hardening. Final results in
the table supersede these intermediate runs. The precision regression was
reproduced on the CLR before the fix: base 450, rate 60 and weight
2.5000833333333333333333333333 produced 540.01 through intermediate decimal
rounding; exact calculation and the new HTTP/database regression require 540.00.

## Phase checkpoint

Stop after Phase 2. Parcel confirmation, tracking generation, atomic booking,
idempotent booking replay, booking outbox publishing, Merchant RPC and customer
client screens remain later phases. No commit, merge, push or changes in other
repositories are authorized for this execution.

Suggested reviewable commits, if separately authorized later:

1. `feat(delivery): add validated owner-bound fee quotes` — application,
   persistence adapter, protected endpoint and safe error handling.
2. `test(delivery): cover quote contracts and PostgreSQL behavior` — calculator,
   application, strict numeric parsing, HTTP/database tests and Gateway smoke.
3. `docs(delivery): record phase two quote behavior and dependencies` — README,
   layer guides and this checkpoint report.
