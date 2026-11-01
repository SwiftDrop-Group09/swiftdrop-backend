# Delivery Phase 7: Merchant booking compatibility

## Before-change assessment

The original master assigns Merchant order integration to Member 7 and permits
a reusable Delivery handler contract, tests and handoff when that adapter belongs
to another developer. This phase implements that compatibility seam. It does not
implement the Merchant inbox, API keys, workload token issuance, gRPC transport or
the Merchant reconciliation processor.

All five repositories were clean before edits. Backend starts at a8cb95b on
test/customer-booking-client-harness, React at 7926824, Flutter at 4d0eecf,
docs at d97fff4 and infra at 075a8b4. The backend base already contains the
previous phases; no main merge is needed. Work proceeds on
refactor/delivery-merchant-booking-compatibility. Other repositories stay read-only.

The master, contribution instructions, current source/configuration/tests,
Phase 6 implementation and current Merchant use case, gRPC/proto, REST, data,
event, security, state-machine and diagram contracts were reinspected. All
master-listed documentation paths and the assignment PDF exist. No AGENTS.md
was found. Proposed DEC/ADR contracts retain that status; the earlier confirmed
Neon and delivery-address tariff choices remain applicable.

Existing Delivery functionality includes Customer quote/confirmation, retained
pricing, tracking generation, atomic history/outbox/replay and isolated client
fixtures. Parcel.ForMerchant and the permanent merchant/external-order unique
index already exist. BookingAssembler shares initial-record construction, but
CustomerBookingHandler still creates the parcel itself and PostgresBookingStore
owns the four-row write directly. The latter transaction consumes a Customer
quote and reserves CreateParcel; it is not a Merchant transaction adapter.

The two unchanged design RPCs are CreateBooking and GetBookingByExternalOrder.
Their backend/docs proto copies match byte-for-byte (SHA-256
2BFD3102819343DB4F2407BF797D12128407B5B39AF27307503381679DCB4FC2).
Service audience is delivery-internal with bookings:create/read scopes. A public
Merchant API key or caller-supplied merchant ID cannot authorize Delivery.
Details and remaining integration decisions are in the
[Member 7 handoff](delivery-merchant-booking-handoff.md).

## Prerequisite verification before edits

SDK 10.0.301 and private PostgreSQL 18 were used; no shared credentials were read.

- Cached restore of all 12 projects passed; online NuGet audit was disabled.
- Release solution build passed with zero warnings/errors (5.78s).
- **530/530 tests passed**, zero failed/skipped: Delivery 439 (7m25s),
  Gateway 68 (2m23s), Identity 12, Merchant 6, Notification 5.
- Delivery/test/harness formatting and EF model-drift verification passed.
- Foundation passed the 12-project reference/cycle audit, all five real hosts,
  missing-feature probes, private listener and protected Gateway POST 401 checks.
- All owned hosts stopped, zero private test directories remained and Git was clean.

## File-level implementation plan

1. Extend Application/Bookings/BookingAssembler.cs with separate Customer and
   Merchant creation entry methods using the same tracking/domain/assembly rules.
   Customer history uses its owner; Merchant requires a trusted audit actor and
   accepted version matching the validated tariff.
2. Route CustomerBookingHandler through that shared creation method while
   preserving its authorization, quote, replay and collision-retry orchestration.
3. Extract the four-row save to Infrastructure/Bookings/BookingWritePersistence.cs,
   requiring an existing transaction before tracking rows. PostgresBookingStore
   retains its reservation/quote guards, commit/rollback and error translation.
4. Add MerchantCompatibility unit/PostgreSQL tests for origins, event privacy,
   retained pricing, commit/rollback, immutable price and unique order/tracking
   behavior. Extend FoundationTests to cover both absent private RPC paths.
5. Record exact Member 7 interface mapping and responsibilities in the handoff;
   update README/layer guides and this report with executed results.

No new package, wire field, endpoint, database table/migration or other-service
reference is planned. Merchant fingerprint canonicalization, workload/business
binding, audit-subject mapping and permanent replay storage need integration-owner
decisions; this seam does not invent those policies or claim live Merchant RPCs.

## Results and checkpoint

The compatibility seam and Member 7 handoff are implemented and verified. The
full regression gate passed with the results below.

### Exact changes

- BookingAssembler.CreateCustomer and CreateMerchant share domain validation,
  tracking-number generation and existing price/history/outbox assembly. Merchant
  requires its accepted version to match the validated tariff ordinally and an
  explicit trusted audit actor. Customer history derives its actor from its owner.
- CustomerBookingHandler uses CreateCustomer. Its authorization, owner-bound
  quote validation, replay, retained pricing, timestamp normalization and three
  tracking-collision attempts are unchanged.
- BookingWritePersistence.SaveAsync requires the caller's explicit transaction
  before tracking rows. PostgresBookingStore uses it after the existing tracked
  quote/reservation guard. The store still owns commit, rollback and exception
  translation; the helper saves the booking-scoped context without committing.
- MerchantCompatibility adds 35 creation unit cases and seven real PostgreSQL
  cases. Foundation adds three cases covering both private RPC paths, including
  POSTs with fictional caller-supplied credentials that still return 404.
- README and Delivery layer guides link the reusable contract. The handoff lists
  the exact ten CreateBooking request fields, ten result fields, service scopes,
  business binding, permanent deduplication and unknown-outcome recovery duties.

No package, project reference, migration, schema, operational seed, secret,
wire asset or routing/listener/authentication registration changed. Identity,
Merchant, Worker and Gateway source and all four sibling repositories are
unchanged. No actual Merchant RPC is implemented or advertised.

### Executed verification

| Check | Observed result |
|---|---|
| Before-change prerequisite suite | 530/530 passed, zero failed/skipped |
| Focused Phase 7/Customer/foundation matrix | 113/113 passed, zero failed/skipped, 27s |
| Final cached restore | All 12 projects passed; NuGet online audit disabled |
| Final Release solution build | Passed, zero warnings/errors, 11.15s |
| Final full solution suite | 575/575 passed, zero failed/skipped: Delivery 484 (7m31s), Gateway 68 (2m23s), Identity 12, Merchant 6, Notification 5 |
| Delivery/test/harness formatting | Passed |
| EF migrations/model comparison | No pending model changes |
| Real five-host foundation smoke | Passed reference/cycle audit, liveness/routing, absent feature/private paths, protected POST 401 and private 8081 listener; all owned hosts stopped |
| Contract assets | delivery.proto and StatusChanged v1 schema unchanged and byte-identical to current docs |
| Independent source/security/handoff review | No material defect or scope mismatch found |
| Documentation links and Git whitespace | Passed |
| Fixture/process cleanup | Zero private PostgreSQL directories and selected test listeners remained |
| Docker | Daemon pipe absent; Docker config inaccessible in this environment; no container build/start result claimed |
| Live Neon, workload JWT, Merchant gRPC/reconciliation, hosted CI | Not executed; adapter-owner integration acceptance remains outstanding |

The focused matrix includes all 35 new creation cases, seven new PostgreSQL
cases, 13 foundation cases and 58 existing Customer booking application cases.
PostgreSQL checks verify explicit-transaction enforcement before tracking,
uncommitted writes invisible to another connection, four-row commit/rollback,
immutable accepted snapshots, same-business order uniqueness, tracking collision
rollback and separate businesses using the same external-order string. These
are real Delivery writes, not a live Merchant RPC or deduplication replay test.

The final run exited 0. All five saved TRX files independently aggregate to
575 passed, zero failed/skipped. Local evidence is retained under ignored
artifacts/phase7-test-results/ (TRX and summary.json),
artifacts/phase7-final-*.log and artifacts/phase7-verification.txt. The earlier
verification session did not retain a completion result; this fresh saved run
establishes the final gate. Generated evidence is not committed.

### Remaining integration and checkpoint

The shared creation/write contract is ready for Member 7. That owner still
implements the internal transport and Delivery Merchant reservation/fingerprint/
result/reconciliation adapter, together with its Merchant inbox processor.
Workload credentials/business binding, canonical fingerprint recipe and audit
subject mapping must be agreed before enabling RPC access. The existing unique
parcel index alone cannot return an original result or compare changed payloads.
Customer authentication remains closed pending actual Identity integration.

Client feature source/commits remain exactly those verified in Phase 6; React
and Flutter checks were not rerun for this backend-only refactor. No new secret
or cross-service database access is required. The unchanged
[development-secret guide](development-secrets.md) remains applicable.

The executed solution checkpoint commands are:

```powershell
dotnet restore SwiftDrop.sln --source 'C:\Users\adith\.nuget\packages' -p:NuGetAudit=false
dotnet build SwiftDrop.sln --no-restore --configuration Release
$env:SWIFTDROP_TEST_POSTGRES_TEMP_ROOT = 'D:\VS code\swiftdrop'
dotnet test tests/SwiftDrop.Delivery.Tests/SwiftDrop.Delivery.Tests.csproj --configuration Release --no-restore --no-build --filter 'FullyQualifiedName~MerchantCompatibility|FullyQualifiedName~FoundationTests|FullyQualifiedName~BookingApplicationTests' --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'
dotnet test SwiftDrop.sln --no-build --no-restore --configuration Release --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal' --logger 'trx;LogFilePrefix=phase7-final' --results-directory artifacts/phase7-test-results
dotnet format SwiftDrop.sln --no-restore --include src/Services/Delivery/SwiftDrop.Delivery.Api tests/SwiftDrop.Delivery.Tests tests/SwiftDrop.Delivery.Harness --verify-no-changes --verbosity minimal
dotnet-ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-foundation.ps1 -Configuration Release
git diff --check
docker info --format '{{.ServerVersion}}'
```

The online package audit, hosted CI and Docker build were not substituted by
these local checks. The installed dotnet-ef executable was used; no local-tool
package restore or live database migration was performed.

The backend compatibility seam, tests and handoff are committed locally as
`refactor(delivery): share booking creation with merchant adapters` on
refactor/delivery-merchant-booking-compatibility. Backend main remains 32604d4;
the four sibling repositories remain clean on their original commits.
Stop at Phase 7. No push, main merge, Merchant module takeover or Phase 8 work
is included.
