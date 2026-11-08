# Courier master-prompt completion audit

## Assessment before edits — 29 October 2026

All five working trees are clean. Backend Courier HEAD is `b027590`, React
Courier HEAD `41380fc`, Flutter Courier HEAD `534c182`; docs/main `d97fff4` and
infra/main `075a8b4`. Previous Phase 8 work was committed externally. This audit
will not stage, commit, push, merge, rewrite migrations or modify another feature.
No AGENTS.md applies; existing contribution instructions apply.

The original master, SRS FR-R01–R05, REST/route contracts, state matrix,
authentication/ownership, test cases, phase reports and current implementations
were reinspected. The approved Active/Reassigned/Delivered/Failed/Cancelled
assignment lifecycle and photo OR signature policy remain unchanged. Proposed
documentation is not relabelled Accepted.

Phases 1–6 have assignment-aware persistence, strict manifest/detail reads,
guarded versioned/replay-safe stage writes, decoded private proof completion,
reason-required terminal failure, and minimal terminal self history. Phase 7 has
both clients with real server state, capture, failure/history and recovery.
Phase 8 supplies concurrency/security regressions and cross-client acceptance
tooling. Prior actual final TRX: 923 Delivery + 68 Gateway + 12 Identity +
6 Merchant + 5 Notification = 1014 passed, zero failed/skipped. Prior React 86,
Flutter 87 and fixture postconditions are recorded separately from fresh runs.

Read-only remote checks confirm Courier `b027590`, Identity `b11c4a6`, and the
tracking/receipt branch `32604d4` (the old Booking baseline, no download adapter).
No fetch or merge is performed. The current Identity checkout is registration
only; full Identity remains separate. Delivery deliberately fails closed.
Master Task 2.2 explicitly permits test-only authentication when Identity is
unavailable. Courier does not own another login, account store, assignment
algorithm or notification publisher. Those boundaries must not be bypassed to
make acceptance appear complete.

Docker's Linux engine is unavailable. Explicit proof-storage settings are all
absent (only their presence was checked). Live storage acceptance remains blocked.
Native tooling will be rechecked. Shared Neon and live bucket changes are not
authorized or needed for the disposable verification.

## Verified remaining implementation work

The shared Flutter HttpRequestTransport currently resolves absolute foreign URLs
and its JSON requests follow redirects. An injected authenticated client could
therefore send credentials away from its configured origin. Multipart already
disables redirects but shares the foreign-URL issue. Valid Booking/Courier calls
all use the established relative routes. Add one common origin/path guard and
disable JSON redirects, preserving those valid requests and all existing ports.
The current data-protection contract requires HTTPS for non-loopback traffic;
enforce that existing rule as well as the Android debug loopback policy.
Both Courier adapters should retain the original request after an unconfirmed
redirect response, as they already do after unexpected 201/202; do not replay a
write through another location or generate a new key blindly.

File plan before feature edits:

- Flutter `lib/core/network/request_transport.dart`: common safe resolution for
  JSON/multipart, no credential-bearing base URL or external references, no JSON
  redirect following. Existing deadlines and same authenticated client retained.
- Flutter `test/core/network/request_transport_security_test.dart`: reproduce
  foreign-target and real loopback redirect behavior; verify valid requests.
- Flutter `lib/features/courier/courier_api.dart` and existing Courier tests:
  retain the immutable original attempt for undocumented redirects.
- Flutter `lib/features/courier/courier_screen.dart`: remove the undocumented
  2000–2100 date-picker restriction; use the backend DateOnly calendar range.
- React `app/features/courier/api.ts` and existing contract tests: same
  unconfirmed-response boundary even for injected request adapters.
- This audit and client integration docs: all-phase source/evidence matrix,
  exact results, contract mismatches and owner/environment dependencies.

No new API route, business state, allocation rule, proof type, event schema,
production backend, migration, Identity implementation or broker relay is planned.
Fresh checks include the full existing solution suite, both client suites/builds,
origin/redirect security regressions, owned cross-client/Chrome verification,
model/schema/preservation checks and explicit blocked live/native checks.

## All-phase implementation and evidence matrix

Historical counts below were read from actual TRX, not inferred from phase prose.
They are successive regression suites, not additive distinct test totals.

| Phase | Verified existing implementation | Historical actual evidence | Fresh verification |
| --- | --- | --- | --- |
| 1 — domain/persistence | Existing Parcel/version, Courier/CourierZone, retained CourierAssignment lifecycle, filtered unique Active owner, PostgreSQL xmin, private ProofMetadata, append-only BookingHistory, additive migrations | `courier-phase1-complete`: 626 passed, zero failed/skipped | All domain, relationship, constraint, migration and PostgreSQL races included in current backend suite |
| 2 — assigned reads | DeliveryReadHandler + PostgresDeliveryReadStore; current Courier self manifest; service day/overdue in Asia/Colombo; safe private detail and real history; count/page authorization snapshot | `courier-phase2-final`: 694 passed, zero failed/skipped | Current read/authentication/query/privacy tests, both real HTTP adapters and Chrome |
| 3 — transitions | CourierTransitionPolicy/StatusHandler and existing transactional store; canonical next stages, private notes, version/current assignment at commit; exact idempotent replay and unchanged v1 outbox | `courier-phase3-final`: 834 passed, zero failed/skipped | Current status/authentication/reliability/migration tests and cross-client state/event checks |
| 4 — proof/completion | Multipart ProofValidator, decoded/normalized JPEG/PNG, private S3 adapter, generated conditional-create keys, atomic Delivered/history/proof/outbox/replay and reference-aware orphan cleanup | `courier-phase4-complete`: 926 passed, zero failed/skipped | Current image/security/storage-double/fault/cleanup tests and photo/signature client completion; live bucket separately blocked |
| 5 — failure | Existing status route, exact four reasons, optional private notes, terminal Failed assignment, version/history/event/replay in one transaction, no invented reassignment/redelivery | `courier-phase5-final`: 961 passed, zero failed/skipped | Current failure API, rollback and concurrent reason/replay tests plus real HTTP failure reports |
| 6 — terminal history | CourierHistoryHandler; retained Delivered/Failed actor ownership, reassigned-away exclusion, assign-back deduplication, stable paging and six-field summaries | `courier-phase6-final`: 1009 passed, zero failed/skipped | Current history authorization/read consistency/privacy tests, HTTP/Chrome history; no closed-detail links |
| 7 — both clients | React existing Identity session/request adapter and Query-scoped views; Flutter existing Navigator/ChangeNotifier, shared session/request ports, camera/gallery and drawn signatures; precise numbers, immutable attempts and session/stale/network recovery | `courier-phase7-regression`: 1009 backend + separately run fixture 1; actual client logs record 85 React/85 Flutter | Fresh 91 React and 118 Flutter tests, both release web builds, actual API adapters and Chrome; new transport/redirect regressions |
| 8 — final verification | Existing owned socket fixture, schema/state verifier, opt-in storage gate, late-denial/unconfirmed-result guards and explicit blocked acceptance metadata | `courier-phase8-final`: 1014 passed, zero failed/skipped; prior client logs 86 React/87 Flutter | Fresh backend 1014, all client checks, foundation, schema/model/SQL, audits and source preservation |

Source was cross-checked against the original master, current SRS/traceability,
use cases, ER/data architecture, state matrix, REST/schema/security, and Courier
sequence/activity diagrams. Only the documented HTTP resources exist:

- GET `/api/v1/couriers/me/assignments` — current Courier, date/overdue/page.
- GET `/api/v1/parcels/{id}` — current assignment or documented owner/operations
  access, protected private DTO and actual status history.
- PUT `/api/v1/deliveries/{id}/status` — current assigned Courier, expectedVersion,
  Idempotency-Key, legal stage/private note or Failed/required reason.
- POST `/api/v1/deliveries/{id}/pod` — current assigned OutForDelivery Courier,
  recipient name, expectedVersion/key and at least one valid photo/signature.
- GET `/api/v1/couriers/me/history` — Courier self terminal summaries, page only.

No new route, status, assignment algorithm, proof rule or event field was needed.
ProofMetadata represents only confirmed committed proof; uncommitted staged
objects remain external cleanup candidates, not invented upload-status rows.

## Completed fixes in this audit

New tests first reproduced 13 transport failures: arbitrary/external URL targets
were accepted, traversal was normalized rather than rejected, and a real GET
redirect reached a second server. The shared Flutter transport now validates the
raw path before URI normalization, checks the configured origin and credential-
free base, enforces the documented HTTPS/non-loopback policy, and disables
redirects for JSON as already done for multipart. Both response paths reject
redirects with a safe transport error; neither follows Location or leaks proof/
credentials to it. Real loopback GET/POST tests prove no second-server request.
No request body/header serialization or Booking/Courier API route is changed.

Both Courier adapters retain the original immutable request/key after any
undocumented success or redirect response; only a valid matching 200 confirms
delivery. Added 301/302/303/307/308 parity cases complement the existing 201/202
cases. The shared transport's exception also preserves Booking's existing
network-uncertain path without changing Booking's business/controller code.

Flutter's date picker no longer imposes an undocumented 2000–2100 restriction;
its explicit selected date covers the backend DateOnly range. The default Today
request still delegates business-day selection to Delivery.

## Files changed

| Repository | Files | Purpose |
| --- | --- | --- |
| mobile/core | `lib/core/network/request_transport.dart` | Same-origin/raw-path/TLS guard shared by JSON/proof, no automatic redirects and safe error boundary |
| mobile/Courier | `lib/features/courier/courier_api.dart`, `courier_screen.dart` | Unconfirmed redirect recovery and contract-aligned calendar range |
| mobile/tests | `test/core/network/request_transport_security_test.dart` (new), `test/courier/courier_test.dart` | 26 origin/config/path/real redirect transport cases, five extra immutable-retry cases |
| mobile/docs | `docs/courier-integration.md` | Transport and recovery handoff |
| frontend/Courier | `app/features/courier/api.ts`, `api.test.ts` | Undocumented redirect handling/parity tests for injected authenticated adapters |
| frontend/docs | `docs/courier-integration.md` | Same unconfirmed-response boundary |
| backend/docs | `README.md`, `docs/courier-master-audit.md` (new) | All-phase evidence, final scope/results and dependency handoff |

No production backend source, entity, configuration, migration, index, constraint,
history model or outbox schema/writer changed. No package/version/lockfile change.
Existing Customer Booking and Identity feature implementations remain intact.
Later integration must reconcile the shared mobile transport/session/app files
with the independent native IAM branch, retaining its token-generation guards
and these origin/binary-upload protections. No overlapping branch was merged
or overwritten, and no teammate communication or approval is inferred.
All 139 tracked backend src files are protected by before/after SHA-256 checks.
Docs and infra repositories remain untouched, as do independent feature branches.

## Fresh commands and actual results

Commands used the existing checksum-verified, repository-pinned SDK 10.0.301,
Node 24.16.0/npm 11.13.0, Flutter 3.47.8/Dart 3.13.7 and PostgreSQL 18. SDK,
package cache and CLI home are in ignored `artifacts/courier-phase8-tools`;
PostgreSQL fixtures use ignored `artifacts/courier-master-postgres`.
SWIFTDROP_TEST_DELIVERY_DATABASE was removed from test processes. No shared
database password, Identity database, AWS profile or ambient bucket was used.

Backend, from its checkout:

```powershell
dotnet restore SwiftDrop.sln --configfile artifacts/courier-phase8-tools/NuGet.Config --disable-parallel
dotnet build SwiftDrop.sln --no-restore --configuration Release
dotnet test SwiftDrop.sln --no-build --configuration Release --logger 'trx;LogFilePrefix=courier-master-final' --results-directory artifacts/courier-master-final
./scripts/verify-foundation.ps1 -Configuration Release
# Existing pinned EF 10.0.9 tool, process-local DOTNET_ROLL_FORWARD=LatestMajor.
./artifacts/courier-phase8-tools/ef/dotnet-ef.exe migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
./artifacts/courier-phase8-tools/ef/dotnet-ef.exe migrations script --idempotent --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --output artifacts/courier-master-migrations.sql
./scripts/verify-courier-clients.ps1 -Browser
dotnet tests/SwiftDrop.Delivery.Harness/bin/Release/net10.0/SwiftDrop.Delivery.Harness.dll --verify-private-storage
dotnet list SwiftDrop.sln package --vulnerable --include-transitive --format json --no-restore
```

Client runner uses existing pinned tools on this process's PATH, the existing
installed Playwright module override, and Vite at 127.0.0.1:5173 with
DEV_GATEWAY_TARGET=http://127.0.0.1:5088. It owns/disposes its PostgreSQL/API fixture.

React: `npm run format:check`, `npm run lint`, `npm run typecheck`,
`npm run test:ci -- --maxWorkers=1`, `npm run build`, `npm audit --json`.
The initial parallel `npm run check` hit six worker-startup timeouts with no tests
executed; it is not reported as passed. The equivalent components and all six
test files passed when executed with one test worker. No timeout or test assertion
was removed and no repository test configuration was weakened.

Flutter: `dart format` on changed Dart files; `flutter analyze --no-pub`;
`flutter test --no-pub`; `flutter build web --release --no-pub --no-wasm-dry-run`;
`flutter doctor -v`; `flutter build apk --debug --no-pub`.
Before the fix, `flutter test --no-pub test/core/network/request_transport_security_test.dart`
recorded the reproducible transport failures. Final tests compile the full guards.

| Verification | Actual final result |
| --- | --- |
| Backend restore/Release build | Passed; zero warnings/errors |
| Full current backend suite | **1014 passed, 0 failed/skipped**: 923 Delivery, 68 Gateway, 12 Identity, 6 Merchant, 5 Notification |
| React | **91 passed**, six files, one worker; format/lint/types/build passed |
| Flutter | **118 passed** including existing 56 Booking/app cases; clean analyzer and release web build |
| Both HTTP clients + Chrome + PostgreSQL | Passed; original-key replay, stale handling, proof/failure/history and responsive signature UI |
| Post-client invariants | 5 parcels: 2 Delivered, 3 Failed, 2 proofs; no Active assignment; 27 history/events and 10 replay rows; unchanged prices and schema-valid private-free unpublished events |
| Foundation | Real hosts/gateway/reference boundaries and fail-closed protected requests passed |
| Model/migration/schema | No EF drift; idempotent SQL generated without deployment; canonical docs/source event schema matches |
| Dependency audits | No reported vulnerable NuGet/npm packages; versions unchanged |
| Live storage | Explicit **blocked exit 3**, missing dedicated bucket/configuration; no live write attempted |
| Android | Build attempted, **blocked exit 1: no Android SDK** |

Fresh logs are `artifacts/courier-master-*`; current TRX are under
`artifacts/courier-master-final`, historical counters in
`courier-master-historical-evidence.json`. The existing client runner records
its fixture-mode outputs in `artifacts/courier-phase8-clients/results.json`;
liveJwtVerified and liveObjectStorageVerified remain false. Such fixture results
do not establish production credentials, encryption deployment or broker delivery.

## Security and concurrency conclusions

Existing commit-time current-assignment, exact Courier role/subject, expected-
version, legal transition, note/reason/proof and retained-replay checks remain
authoritative. The final backend suite exercises forged identifiers/headers,
anonymous/wrong-role/foreign/former Courier access, stale/concurrent writes,
storage/DB faults, rollback/orphan recovery, immutable history/prices/proof and
transactional events. Both clients preserve precise versions/prices, require a
fresh action after stale reload, never force Delivered through status PUT and
never infer completion from upload progress. Signature/photo data remains
temporary; no new token store or login, actor selector or public object URL exists.

Origin and redirect hardening adds a transport boundary, not an authorization
substitute. It does not grant any operation or change a backend error contract.
The new same-key tests protect uncertain outcomes rather than silently replaying
writes through redirects. No SRS reason, state or schema was silently substituted.

## Documentation mismatches and remaining integration dependencies

Proposed approval markers remain Proposed. Minimal history does not include a
completion timestamp/private proof/reason projection or undocumented filters.
The assigned Courier workflow does not own dispatcher allocation or notification
delivery. Diagram steps after outbox commit (relay/RabbitMQ/Worker/SSE/webhooks)
are wider project integration, not implemented Courier side effects. Customer
proof streaming is a separately documented owner/Admin capability, not permission
to give a Courier unrestricted downloads or to invent a two-image selection API.

Live acceptance still needs independently supplied/owned work:

- Identity's unmerged complete backend and verified Delivery JWT adapter with
  agreed issuer/audience/trusted JWKS, Courier provisioning and the documented
  downstream revocation bound. Current hosts remain correctly fail-closed under
  the master's unavailable-Identity rule; no merge or alternate login was used.
- Native IAM authenticated multipart support on the existing secure lifecycle;
  the current Courier app consumes injected session/transport ports. Do not bypass
  the separate JSON-only IAM adapter with an unauthenticated second client.
- Private S3/MinIO bucket, required encryption/KMS/IAM/TLS and anonymous denial.
  No settings or dedicated test bucket confirmation were provided; SDK doubles
  and memory fixture storage are explicitly different evidence.
- Docker/Linux native startup; Android SDK/device and iOS/Xcode/camera/gallery/
  cache/lost-activity checks. The current machine has web browsers, no physical
  Android/iOS acceptance environment and no Windows C++ toolchain.
- Authorized Customer/Admin proof downloads, reviewed Neon rollout/grants/TLS,
  and outbox relay/notification consumer acceptance. These were not implemented
  by copying independent features or writing another message publisher.

All eight phases' scoped implementation and available verification have been
audited; the concrete remaining client security fixes above are completed.
**This is not a claim of fully accepted live deployment.** The master has no
Phase 9. Next recommended checkpoint is the integration-owner/environment handoff
above and rerun of the existing live acceptance gate. No staged changes, commit,
push, merge, shared migration or unrelated feature modification is included.
