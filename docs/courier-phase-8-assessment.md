# Courier Phase 8 — integration, security and final verification

## Assessment before edits — 29 October 2026

Phase 8 only. All five working trees are clean at entry. Backend is on
`feat/courier-workflow-persistence` at `3c8feeb`; React on
`feat/courier-workflow-clients` at `fed25cd`; Flutter on the same named feature
branch at `28b2d2c`. Docs/main remains `d97fff4`, infra/main `075a8b4`.
The Phase 7 changes were committed externally after the previous checkpoint.
This run will not stage, commit, push or merge.

The master Phase 8, contribution instructions, SRS FR-R01–R05, TC-R01–R05,
security/negative/recovery cases, REST/state-machine/authorization/authentication
contracts and Phase 2–7 implementation reports were reinspected. No applicable
AGENTS.md was found. Documentation remains Proposed. Existing approved assignment
lifecycle and photo OR signature policy remain unchanged.

Prior actual artifacts verify 1009 backend regressions plus one separately run
new fixture test; React 85 tests and build; Flutter 85 tests, analysis and release
web build. Phase 7 source and current commit diffs were inspected, including
immutable attempts, exact numbers, role/session guards and private proof capture.
Fresh Phase 8 runs will be reported separately from those prior artifacts.

Read-only remote head checks confirm backend Courier `3c8feeb`, Identity
`b11c4a6`, Customer tracking/receipt branch `32604d4`. No fetch/merge occurs.
The Courier commit changes tests/harness/docs only relative to Phase 6; production
Delivery is unchanged. The tracking/receipt branch name initially suggested
download work, but inspecting its tree/commit corrected that inference: `32604d4`
is only the older booking-confirmation baseline, with no download adapter. Its
diff against Courier removes later work and must not be treated as an approved
integration merge. Identity's complete implementation is on
a separate branch; this checkout has registration only. Delivery still uses
UnconfiguredIdentityHandler and fails closed. A live Courier JWT acceptance
claim cannot be established by its fixed-principal fixture.

Docker CLI exists but its Linux engine pipe is absent. All five required
ProofStorage environment settings are absent (values were not read/logged).
No local MinIO binary or supplied dedicated bucket is available. Live encrypted
storage/TLS/IAM/anonymous-denial checks must be reported blocked separately from
SDK doubles and PostgreSQL tests. Native toolchains will be reinspected.

## Planned verification work

- Add focused parity/denial-race regressions and fix any reproduced client issue,
  preserving existing Identity/Booking ports and all production Delivery behavior.
- Extend the explicit test harness with a read-only post-client database/outbox
  verifier, reusing the existing event schema and test-only seed. No API route.
- Add an opt-in live private-storage acceptance command with explicit dedicated
  bucket confirmation, generated test keys, no ambient credentials or bucket/
  policy creation, safe cleanup, and a distinct blocked exit when unconfigured.
- Add a reproducible owned-fixture cross-client verification script and test
  coverage for its aggregate invariants; do not mistake fixture auth/storage
  for live integration. Reuse existing React/Flutter adapters and smoke scripts.
- Restore/build/test all existing services/clients; run foundation, migration
  model drift/idempotent script, schema, authorization, races and regression
  checks on disposable PostgreSQL. Re-run Chrome and both real HTTP clients.
- Record exact commands, final counts, preserved files, acceptance matrix and
  remaining blockers. No new production routes, states, assignment policy,
  proof requirement, event schema, migration, auth bypass or unrelated merge.

## Implemented findings and verification tooling

Two client issues were reproduced before fixes. React's new denial-race test
returned `success` and retained a late receipt after `denyRead`; its workflow now
advances a generation on access denial/disposal and ignores both late success
and failure. Flutter's new 201/202 tests found that an unconfirmed response
discarded the original idempotency key; unexpected non-200 success now remains
uncertain, preserving the original immutable payload/key for explicit retry.
Neither client accepts an undocumented success as delivery confirmation. React's
unconfirmed-response message also directs retry of that original request.

The existing socket fixture gained a read-only `verify-courier` console command.
After both existing clients run it checks their actual PostgreSQL state, exact
v1 schema, one event/history per version, retained prices, terminal assignment
ownership, proof references/types, replay count and unpublished outbox. The gate
rejects incomplete work. Its regression also proves that the existing
`ux_outbox_parcel_version_event` constraint rejects an injected duplicate event.
It does not add an API or allocation rule; its counts describe only TESTDATA.

`scripts/verify-courier-clients.ps1` runs the existing React/Flutter adapters and
optional Chrome check against one owned fixture, validates postconditions and
stops the fixture. It checks the pinned Node line to prevent silent fallback to
an unsupported executable, records actual process exit codes and writes results
whose live JWT/storage flags are explicitly false. Dart informational stderr is
captured without turning successful work into a PowerShell error.

An opt-in `--verify-private-storage` console mode uses only explicit existing
ProofStorage settings and a matching dedicated test-bucket confirmation. Missing
configuration returns blocked exit 3, not success. A configured run checks the
real adapter's private/encrypted conditional write/readback, integrity, listing,
anonymous denial and cleanup of an invocation-owned key. It never creates bucket
policies, reads ambient credentials or weakens encryption/TLS. Guard tests verify
missing/mismatched confirmation and unsafe endpoints without network fallback
or secret output. No live storage positive result is claimed in this environment.

## Files changed in this phase

| Repository/module | Files | Change |
| --- | --- | --- |
| React Courier | `app/features/courier/workflow.ts`, `api.ts`, `api.test.ts` | Denial generation guard, safe original-retry message and reproduced race regression |
| React documentation | `docs/courier-integration.md` | Denial/unconfirmed-result contract clarification |
| Flutter Courier | `lib/features/courier/courier_api.dart`, `test/courier/courier_test.dart` | Unconfirmed-success recovery parity and two original-key retry regressions |
| Flutter documentation | `docs/courier-integration.md` | Same committed-200 recovery boundary |
| Backend test fixture | `tests/SwiftDrop.Delivery.Tests/ClientHarness/BookingClientHarness.cs` | Partial declaration for isolated verifier; existing behavior retained |
| Backend test fixture | `ClientHarness/CourierClientVerification.cs`, `CourierClientVerificationTests.cs` | Post-client database/schema/side-effect verifier and real PostgreSQL regression |
| Backend acceptance | `ClientHarness/PrivateStorageAcceptance.cs`, `PrivateStorageAcceptanceTests.cs` | Opt-in private-bucket check, explicit blocked result and configuration/secret guards |
| Backend console | `tests/SwiftDrop.Delivery.Harness/Program.cs` | Explicit QA commands only; normal Booking/Courier fixture modes retained |
| Backend scripts | `scripts/verify-courier-clients.ps1` | Owned cross-client/Chrome/database runner with cleanup and honest result metadata |
| Backend docs | `README.md`, `docs/courier-verification.md`, this file | Repeatable commands, actual results, ownership/deployment boundaries and handoff |

No production backend source changed. SHA-256 preservation checks confirm all
**139 tracked src files unchanged**, including migrations, models, event schema,
authentication, Booking, status/history, proof handler/storage and outbox writers.
No entities, EF configurations, migrations, constraints, production packages or
API contracts were added/changed. Docs and infra repositories remain untouched.
SDK-generated header newline noise on Flutter was normalized back to its original
checkout form; plugin/package configuration is unchanged.

The five production Courier endpoints remain GET `/api/v1/couriers/me/assignments`,
GET `/api/v1/parcels/{id}`, PUT `/api/v1/deliveries/{id}/status`, POST
`/api/v1/deliveries/{id}/pod`, GET `/api/v1/couriers/me/history`. Their verified
roles, current assignment/version guards, multipart/notes/reason contracts,
minimal receipts and paging are unchanged. The console commands/result JSON are
verification interfaces, not production HTTP routes or event schemas.

## Exact final commands and environment

The inherited temporary SDK/Node caches became incomplete during verification.
Their binaries/runtime assets disappeared; this was not an application change.
The pinned .NET 10.0.301 and Node 24.16.0 archives were restored from official
release metadata into ignored `artifacts/courier-phase8-tools`, with SHA-512 and
SHA-256 checks respectively. No system installation, registry or global setting
changed. NuGet config/cache and PostgreSQL fixture root were also kept in ignored
workspace artifacts for subsequent runs. Flutter remains 3.47.8/Dart 3.13.7.

Final backend commands ran from its checkout with these process-local settings:

```powershell
$tools = Join-Path $pwd 'artifacts/courier-phase8-tools'
$env:DOTNET_CLI_HOME = Join-Path $tools 'cli'
$env:DOTNET_ROOT = Join-Path $tools 'dotnet'
$env:NUGET_PACKAGES = Join-Path $tools 'packages'
$env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
$env:SWIFTDROP_TEST_POSTGRES_TEMP_ROOT = Join-Path $pwd 'artifacts/courier-phase8-postgres'
Remove-Item Env:\SWIFTDROP_TEST_DELIVERY_DATABASE -ErrorAction SilentlyContinue
dotnet restore SwiftDrop.sln --configfile (Join-Path $tools 'NuGet.Config') --disable-parallel
dotnet build SwiftDrop.sln --no-restore --configuration Release
dotnet test tests/SwiftDrop.Delivery.Tests --no-build --configuration Release --filter 'FullyQualifiedName~CourierClientVerificationTests|FullyQualifiedName~PrivateStorageAcceptanceTests' --logger 'trx;LogFilePrefix=courier-phase8-targeted-pass' --results-directory artifacts/courier-phase8-targeted-pass
dotnet test SwiftDrop.sln --no-build --configuration Release --logger 'trx;LogFilePrefix=courier-phase8-final' --results-directory artifacts/courier-phase8-final
./scripts/verify-foundation.ps1 -Configuration Release
dotnet tool install dotnet-ef --version 10.0.9 --tool-path (Join-Path $tools 'ef') --configfile (Join-Path $tools 'NuGet.Config')
# Process-local roll-forward lets the tool entry run on the available .NET 10 runtime.
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'
& (Join-Path $tools 'ef/dotnet-ef.exe') migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
& (Join-Path $tools 'ef/dotnet-ef.exe') migrations script --idempotent --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --output artifacts/courier-phase8-migrations.sql
dotnet list SwiftDrop.sln package --vulnerable --include-transitive --format json --no-restore
dotnet tests/SwiftDrop.Delivery.Harness/bin/Release/net10.0/SwiftDrop.Delivery.Harness.dll --verify-private-storage
./scripts/verify-courier-clients.ps1 -Browser
```

The tools NuGet config contains only the public nuget.org source. The client
runner additionally received pinned Node and Flutter bin on PATH, and an installed
Playwright module through its existing override. Its Vite dependency ran with
`DEV_GATEWAY_TARGET=http://127.0.0.1:5088` and
`npm run dev -- --host 127.0.0.1 --port 5173`.

React: `npm ci`, `npm run check`, `npm audit --json`. Flutter:
`flutter pub get --enforce-lockfile`, `flutter analyze --no-pub`,
`flutter test --no-pub`, `flutter build web --release --no-pub --no-wasm-dry-run`,
`flutter doctor -v`, `flutter build apk --debug --no-pub`.
Changed C# files were checked with `dotnet format whitespace SwiftDrop.sln
--no-restore --verify-no-changes --include` followed by the six changed/new
harness/test C# paths listed above. Event schemas were compared after newline
normalization, and final `git diff --check`/protected-source hashes were checked.

## Actual results and acceptance matrix

| Check | Actual result | Boundary |
| --- | --- | --- |
| Backend restore/Release build | Passed; **zero warnings/errors** | Pinned SDK, no production source changes |
| Final full solution tests | **1014 passed, 0 failed, 0 skipped** | Delivery 923, Gateway 68, Identity 12, Merchant 6, Notification 5 |
| New backend targeted tests | **4 passed** | Included again in the full 1014, not additional distinct cases |
| React check | Passed; **86 tests**, lint/typecheck/format and production SPA build | Existing Identity tests preserved |
| Flutter check | Passed; **87 tests**, no analyzer issues, release web build | Existing 56 Booking/app tests preserved |
| Chrome + both real HTTP adapters | Passed | Explicit fixture auth and memory storage, not live JWT/bucket |
| Post-client database state | Passed: 5 parcels, 2 Delivered, 3 Failed, 2 proofs, 27 history/outbox, 10 replay rows | No active assignments; immutable retained prices; schema/version/privacy and exact replay checked |
| Foundation hosts/gateway | Passed; 12-project boundary/cycle check, five hosts and protected forwarding | Placeholder auth correctly returns 401; not token acceptance |
| EF model drift / idempotent SQL | Passed; no pending changes, SQL generated | No shared Neon migration applied |
| Authoritative event schema | Passed; docs/source match after newline normalization | Actual stored v1 events validated |
| Dependency audits | NuGet query and npm audit passed; **no reported vulnerable packages** | Not a claim about unexecuted platform/deployment acceptance |
| Live private storage | **Blocked, exit 3**; five settings and dedicated bucket confirmation absent | SDK-double tests passed separately; no live encryption/IAM/TLS/anonymous result claimed |
| Docker/Linux native decoding | **Blocked**; Docker engine pipe unavailable | Windows decoder tests do not establish Linux container startup |
| Android build | **Blocked, exit 1**: No Android SDK found | Doctor also reports missing Windows C++ toolchain; no physical Android device |
| iOS/device camera/gallery/cache behavior | **Not run** | No Xcode/device; widget/HTTP/web tests are distinct evidence |
| Real Courier JWT / Delivery validation | **Blocked** | Current Identity is registration-only; Delivery uses fail-closed placeholder |
| Native Flutter IAM multipart | **Blocked** | Separate unmerged IAM transport is JSON-only; no alternate login/client added |
| Customer/Admin proof streaming | **Blocked/unimplemented** | No download adapter in current checkout or named tracking branch; missing route is not authorization acceptance |
| Broker relay/notification delivery | **Blocked/unimplemented** | Outbox persists and remains unpublished; no relay/consumer delivery claim |
| Shared Neon deployment/grants/TLS | **Not run** | No shared deployment requested or performed |

Initial new regressions failed before the fixes, as recorded in
`courier-phase8-react-reproduction.txt` and `courier-phase8-flutter-reproduction.txt`.
The added backend test initially used an untranslatable LINQ predicate, then
incorrectly expected to insert a duplicate already forbidden by the existing
index. Both test issues were corrected; the final targeted/full runs passed.
Intermediate tool-cache startup failures are not passing builds. Final evidence
is in `artifacts/courier-phase8-*`, final TRX, client `results.json`, preserved
source hashes and generated migration SQL. Blocked/unrun checks remain separate.

## Security, concurrency and preserved behavior

The final suite covers anonymous/forged headers and bearer attempts, Customer/
wrong-role operations, Courier A/B and old assignment ownership, trusted subject
validation, unknown authorization fields, stale versions, invalid transitions,
direct Delivered rejection, missing proof/reason, malformed/MIME/oversized images,
private ACL/policy/integrity failures, storage/DB failure rollback, orphan cleanup,
concurrent status/failure/proof/reassignment races and exact same-key replay.
These existing backend guards were reused without changing state/event rules.
The new client regressions add denial-race and unconfirmed-receipt recovery.

Customer estimate/confirmation, tracking generation, price snapshots, Booking
idempotency, Merchant compatibility, append-only history, retained assignment
lifecycle and transactional outbox regression all passed. Client gates remain
advisory: backend ownership/version checks still occur at commit. No raw secret,
storage URL or private request body is added to logs/events. Proof stays temporary
on clients and references remain private. No alternate authentication bypass,
shared Identity database access, automatic new-key write retry or production
allocation API was introduced. Data mutation for verification is confined to
explicitly owned fictional fixtures; the live bucket check did not run writes.

## Mismatches, owner handoff and final checkpoint

The repository still has Proposed documentation and unmerged implementation
branches. History remains the six-field terminal projection; no private historical
detail, proof URL, new filter or event field was invented. The Native Identity
branch's name does not imply it has no code, while the tracking/receipt branch's
name does not imply a download implementation; both trees were checked.

Full live acceptance remains blocked on agreed issuer/audience/trusted JWKS and
verified Delivery authentication, actual Courier/profile provisioning and the
documented 15-minute downstream revocation bound; Flutter IAM multipart on its
existing secure session lifecycle; approved private storage/KMS/IAM/TLS and
container-native decoding; native device capture/cache checks; authorized proof
download; Neon rollout/role validation; broker relay and notification consumers.
No missing environment value, route, event schema or actor rule was fabricated.

Phase 8 verification is completed for the available dependencies; **the complete
workflow is not accepted for live deployment**. Phase 8 is the master's final
numbered phase. The next recommended checkpoint is the integration-owner handoff
above, followed by rerunning Phase 8 live acceptance after dependencies are supplied.
No Phase 9, merge, shared deployment, staging, commit or push is authorized here.
