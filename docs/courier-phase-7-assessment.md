# Courier Phase 7 — client implementation

## Pre-implementation assessment (2026-10-29)

Scope: Phase 7 only, React and Flutter Courier clients. No commit, push or merge.
Backend starts at `8722d6e` on `feat/courier-workflow-persistence`; documentation
at `d97fff4`, infrastructure at `075a8b4`. Phase 6's actual TRX artifacts report
1009 passing tests (918 Delivery, 68 Gateway, 12 Identity, 6 Merchant,
5 Notification), no failures or skips. Delivery's current manifest, private
detail, status, proof and terminal history implementations were inspected.

The user selected React Identity branch `e3c75a7` as the client base. The new
`feat/courier-workflow-clients` branch extends it without merging the divergent
Customer Booking branch `7926824`. Its lossless JSON utility will be reused;
its booking implementation and branch remain untouched. React reuses
IdentitySession.authenticatedRequest, root providers and existing account UI.
Flutter extends Customer Booking main `b819b42` on a separate branch with the
same feature branch name. Its injected session and authenticated transport ports
remain the integration boundary. Verification corrected the initial assumption
about the separately maintained Identity branch: despite its handoff name it
contains native Identity code (`8d2d9d4`). It is absent from this Booking main base.
No merge is authorized. Its RequestTransport/CustomerSession seams match the
existing ports; its IdentityAuthenticatedTransport currently supports JSON send
only and needs the optional multipart capability connected by the IAM owner.
Existing booking behavior and tests must continue to pass.

Authoritative inputs: master prompt Phase 7, SRS FR-R01–R05, REST contract,
parcel state machine, authorization matrix, TC-R01–R05, implemented Phase 2–6
contracts and assessments. Proposed document approvals are not changed.
Use only GET couriers/me/assignments, GET parcels/{id}, PUT deliveries/{id}/status,
POST deliveries/{id}/pod and GET couriers/me/history under /api/v1.
History exposes only common parcel summaries, without links to closed private
details. No invented assignment APIs, allocation rules, states or events.

Versions remain lossless signed-64-bit integers; prices retain decimal digits.
Mutations freeze version, payload, proof bytes and idempotency key. Uncertain
outcomes allow explicit retry of the same request. Stale/invalid transitions
reload state and require a new action. Proof requires a photo or signature
(both accepted), recipient name and OutForDelivery. Delivered is confirmed only
by the backend response. Session changes clear private state and proof.

Planned files: feature-local Courier API/models/controller/UI/tests in each
client, Courier routes/navigation, shared exact-number/session/transport adapters
where needed; no production backend, migration, history or outbox changes.
Verification: client checks/builds/tests, owned PostgreSQL regression tests and
client HTTP checks where dependencies are available. Production Identity still
fails closed pending verified issuer/JWKS integration. Live private object
storage, Docker and mobile platform toolchains require independent verification;
test fixtures cannot establish those deployment claims.

## Implemented outcome

Both existing applications now contain Courier manifest, assigned parcel detail,
legal stage/failure actions, version-aware proof completion and minimal terminal
history. Manifest supports service day, overdue inclusion and paging; history
uses only documented paging. Screens provide loading, empty, validation, denied,
not-found, stale and network recovery states. Detail history comes from Delivery.
Terminal summaries do not link to private detail routes.

React uses the existing authenticated request adapter and account navigation.
Flutter uses the same injected session and transport ports as Booking, with an
optional multipart capability on the existing client and a backward-compatible
CustomerSession alias. Camera/gallery and drawn PNG signatures are implemented;
photo, signature or both are accepted. Private files remain in temporary memory;
owned native cache copies are removed within a resolved temporary-directory
boundary. Lost captures are discarded, never rebound to another identity.

Writes freeze their version, body/files and random key. Only explicit identical
retries are permitted after uncertain results; double submissions/new actions
are blocked. Stale/invalid transitions reload before a new action. Session/role
changes clear private UI/pending proof and ignore old results. Matching backend
receipts alone confirm Delivered; upload progress does not. React uses an
indeterminate wait because the existing fetch adapter provides no byte progress;
Flutter reports multipart progress and then waits for confirmation. Multipart
uses the existing HTTP client, disables redirects and has one abortable
120-second deadline. Existing Booking JSON transport behavior is preserved.

Real Chrome checks found and verified fixes for the development effect cleanup
probe and narrow-viewport fieldset overflow. React UI tests now run in StrictMode.
Both clients' actual signature drawing/export paths are exercised, in addition
to contract tests for photo/signature/both and missing proof. At OutForDelivery,
the note field is explicitly a failure note: the pod contract has no notes field.

## Files changed

Paths in this table are relative to their named repositories. New feature files
are intentionally isolated; no existing authentication implementation is replaced.

| Repository | Files | Purpose |
| --- | --- | --- |
| frontend | `app/features/courier/api.ts`, `workflow.ts` | Strict lossless DTOs, verified API facade, immutable attempts/recovery |
| frontend | `app/features/courier/screens.tsx`, `styles.css` | Manifest/details/history, stage/failure/proof controls, signature capture, responsive UI |
| frontend | `app/features/courier/api.test.ts`, `screens.test.tsx`, `test-fixtures.ts` | Contract, privacy, concurrency and StrictMode UI tests |
| frontend | `app/portals/courier/layout.tsx`, `manifest.tsx`, `details.tsx`, `history.tsx`, `app/routes.ts` | Existing router integration |
| frontend | `app/features/identity/components.tsx` | Courier navigation link only; authentication/session code unchanged |
| frontend | `app/shared/exact-json.ts` | Reuse of Customer Booking branch's existing exact-number codec |
| frontend | `test_support/courier-http-smoke.mjs`, `courier-browser-smoke.mjs` | Explicit real-HTTP and Chrome fixture checks; absent from production routes |
| frontend | `README.md`, `docs/courier-integration.md` | Contracts, handoffs, checks and limits |
| mobile | `lib/features/courier/courier_models.dart`, `courier_api.dart`, `courier_controller.dart` | Exact DTOs, verified routes, actor-scoped state and recovery |
| mobile | `lib/features/courier/courier_screen.dart` | Existing Navigator/ChangeNotifier UI, real signature PNG export |
| mobile | `lib/features/courier/proof_capture.dart`, `proof_cleanup_io.dart`, `proof_cleanup_stub.dart` | Camera/gallery, bounded immutable bytes, scoped cache cleanup |
| mobile | `lib/core/session/app_session.dart`, `customer_session.dart` | Shared injected snapshot and compatible old type alias |
| mobile | `lib/core/network/request_transport.dart` | Optional multipart port/progress/deadline on the same HTTP client; old send interface preserved |
| mobile | `lib/app/app.dart`, `lib/features/customer/booking/presentation/booking_screen.dart` | Shared injection, Courier route and navigation; booking form/controller unchanged |
| mobile | `pubspec.yaml`, `pubspec.lock`, generated Linux/Windows plugin `.cc`/`.cmake` files and macOS `GeneratedPluginRegistrant.swift` | Pinned image_picker/path_provider and SDK-generated plugin registration |
| mobile | `ios/Runner/Info.plist`, macOS `DebugProfile.entitlements`, `Release.entitlements` | Scoped photo/camera descriptions and file/network access |
| mobile | `test/courier/courier_test.dart`, `test_support/courier_http_smoke.dart` | API/controller/widget/security tests and explicit real-HTTP check |
| mobile | `README.md`, `docs/courier-integration.md` | Shared IAM/Booking integration and native limits |
| backend | `tests/SwiftDrop.Delivery.Harness/Program.cs`, `tests/SwiftDrop.Delivery.Tests/ClientHarness/BookingClientHarness.cs` | Optional `--courier-workflow` fixture; normal Booking mode unchanged |
| backend | `tests/SwiftDrop.Delivery.Tests/ClientHarness/BookingClientHarnessTests.cs`, `Courier/StatusTestScenario.cs` | One new real-HTTP fixture test and reuse of existing test-only seed logic |
| backend | `docs/courier-phase-7-assessment.md` | Before-edit assessment and actual implementation/verification record |

No production database/API changes. No new production route, event, migration,
parcel/assignment state, proof rule or outbox behavior. The new CLI switch only
seeds fictional parcels in an owned disposable PostgreSQL cluster and installs
existing test-only proof storage. The existing fixture context URL is reused;
no normal host registers it or accepts a caller-selected test identity.

## Actual verification results

Pinned Node 24.16.0/npm 11.13.0, Flutter 3.47.8/Dart 3.13.7 and .NET SDK 10.0.301
were used. PostgreSQL integration uses owned private PostgreSQL 18 clusters;
SWIFTDROP_TEST_DELIVERY_DATABASE is cleared. No shared Neon deployment occurs.

| Check | Actual result |
| --- | --- |
| React `npm ci` | Passed; 273 packages audited, zero reported vulnerabilities |
| React `npm run check` | Passed: formatting, strict lint/typecheck, **85 tests**, production SPA build |
| Flutter `pub get --enforce-lockfile` | Passed after ignored local plugin-junction workaround; no global settings changed |
| Flutter `analyze --no-pub` | Passed, no issues |
| Flutter `test --no-pub` | **85 passed** (56 existing Booking/app tests and 29 Courier tests) |
| Flutter normal `build web --release --no-pub --no-wasm-dry-run` | Passed |
| Flutter `build apk --debug --no-pub` | Attempted; blocked: **No Android SDK found** |
| Flutter doctor | Chrome/Edge available; Android SDK and Windows C++ toolchain unavailable. No native emulator/device or Xcode validation |
| Backend Release build | Passed, **zero warnings/errors** |
| Existing full solution regression | **1009 passed, 0 failed/skipped**: Delivery 918, Gateway 68, Identity 12, Merchant 6, Notification 5 |
| New isolated Courier fixture test | **1 passed**, real HTTP manifest/proof/history and terminal private-detail denial |
| React production adapter HTTP smoke | Passed: real PostgreSQL stages, lost-response exact replay, stale rejection, photo completion/replay, failure reason, terminal privacy/history |
| Flutter production adapter HTTP smoke | Passed: real PostgreSQL stages/replay, signature multipart/progress/completion/replay, failure reason, terminal privacy/history |
| Chrome 390px browser smoke | Passed: real Delivery manifest/details/history, missing-proof validation, pointer signature selection, no horizontal overflow or page errors |
| Final Git/whitespace review | Passed; backend `git diff HEAD -- src` empty; docs/infra clean; feature branches remain uncommitted |

Logs/TRX/screenshots are in ignored `artifacts/courier-phase7-*` and
`artifacts/courier-phase7-browser`. The baseline regression ran before the added
fixture test; the final rebuilt fixture was then checked separately. Those are
distinct actual runs, not a claim that the final suite ran as one 1010-test run.
An intermediate backend rebuild encountered a testhost assembly lock while the
regression was active; the final serialized rebuild passed. Initial client/test
failures were corrected and final commands above passed. Android is the remaining
attempted build failure, explicitly environmental rather than a passing check.
Owned fixture/dev servers were stopped after verification.

## Security, concurrency and preservation

Client tests cover wrong roles/no calls, revoked/lost ownership, session switches,
late completions, double submission, immutable proof bytes, version/price
precision, explicit failure reasons, proof requirements and uncertain retries.
Existing backend regression verifies anonymous/wrong-role/cross-Courier access,
retained assignment ownership, optimistic-version races, atomic proof/history/
outbox writes and idempotency. No client gate substitutes for backend enforcement.
No raw server/private error detail, token, bucket credential or public object URL
is displayed/logged by the feature. Browser fixtures stub Identity only in the
test script; normal app code has no test identity or developer bypass.

Final local Git audit confirms backend HEAD `8722d6e` and no production Delivery
diff. Customer Booking form/controller/API logic, Identity session/authentication,
status history, event schemas, migrations and outbox writers are preserved.
Only a Booking navigation entry and shared-compatible ports change on mobile.
React Identity is the explicitly selected base; divergent Booking is not merged.
Inspection uses local commits and available local remote-tracking refs, not a
claim about unseen remote/unpushed developer work. No staging, commit, push,
merge, global settings or unrelated workspace changes were performed.

## Documentation mismatches and remaining dependencies

- Docs remain Proposed. The existing approved assignment lifecycle and photo OR
  signature policy are retained without changing approval markers.
- REST common Parcel summaries and FR-R05 do not include private historical
  detail, proof links, failure reason projection or date/status history filters.
  Clients therefore show minimal history and do not invent those fields/routes.
- The API has no proof-completion notes field; private notes belong to stage or
  failure actions. Failed's explicit reason is always sent and persisted by the
  existing backend, even though terminal summary history cannot display it.
- The Flutter Identity branch name/document's historical handoff wording obscured
  its actual native implementation. It was reinspected; its JSON port is
  compatible but authenticated multipart is a remaining IAM integration change.
  No second login/client or unauthorized merge was used to conceal that gap.
- React Booking and Identity branches diverge. The user selected Identity; a
  later explicitly authorized integration must reconcile the Booking portal.
- Real Identity issuer/JWKS/audience/expiry validation, Courier role/provisioning
  and revocation must still be connected to Delivery, which currently fails
  closed. Test principals do not establish JWT acceptance.
- Reviewed Neon migration rollout/grants/TLS, encrypted private bucket KMS/IAM/
  TLS/lifecycle acceptance, Docker deployment and broker/outbox consumers remain
  prior integration dependencies. No live bucket or broker claim is made here.
- Native camera/gallery/cancellation, lost-activity recovery, actual device cache
  cleanup, Android packaging/emulator and iOS/Xcode/Keychain/device behavior need
  native verification. Widget/web/HTTP tests cannot establish those claims.

Next recommended phase: **Phase 8 — Integration, Security and Final Verification**,
after explicit instruction, including the IAM multipart handoff and real-service/
native acceptance checks above. Stop after Phase 7; no Phase 8 implementation,
commit, push or merge is included.
