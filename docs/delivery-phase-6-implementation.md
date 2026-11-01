# Delivery Phase 6: independent Customer clients

The Customer quote and booking features are implemented in React and Flutter,
with a separate real-HTTP test harness to verify them independently of IAM.
Normal applications require the IAM member's real session/request adapters.

## Scope and baseline

The user clarified that IAM is owned by another member and approved independent
booking implementation through replaceable adapters and isolated test entry
points. The prior documentation-only checkpoint was 5ee5420. This execution
implements the Phase 6 feature rather than waiting for IAM or creating a second
authentication system.

The actual starting repositories were clean: backend 5ee5420, frontend c062b11,
mobile e9778c1, docs d97fff4 and infra 075a8b4. The original master, current
contracts, contribution guidance, source and previous implementations were
reinspected. No AGENTS.md was found. No merge into main is necessary because
the backend working base already contains the preceding phases.

The backend branch is test/customer-booking-client-harness. Both client
repositories use feat/customer-quote-booking. Docs and infra remain read-only.

Before feature edits:

- Backend cached restore, Release build (zero warnings/errors, 9.90s), EF drift,
  source/test formatting and real five-host Gateway smoke passed.
- Backend **526/526 tests passed**, zero failures/skips: Delivery 435 (7m39s),
  Gateway 68 (2m22s), Identity 12, Merchant 6, Notification 5. Private PostgreSQL
  cleanup completed and Git remained clean.
- React used a portable official SHA-256-verified Node 24.16.0/npm 11.13.0 in
  workspace .tools. npm ci preserved the lockfile; the existing full npm run
  check passed, including the original 1/1 test and production build. Installed
  global Node 26.12.1/npm 12.2.1 and repository pins were not changed.
- Flutter 3.47.8/Dart 3.13.7 satisfy the existing SDK constraint. Locked package
  restore, analysis (no issues) and the original 1/1 widget test passed. Tool-only
  generated line-ending changes were inspected and restored before feature work.
- Flutter has Chrome/Edge but no Android SDK/device or Windows C++ toolchain.
  Android APK/device acceptance remains an external check; implementation and
  browser/VM verification proceed without claiming it passed.

## Implementation boundaries

The implemented file boundaries are:

| Area | Implementation |
|---|---|
| Backend test infrastructure | tests/SwiftDrop.Delivery.Tests/ClientHarness, private-only initialization in PostgresFixture, standalone tests/SwiftDrop.Delivery.Harness runner, solution and explicit foundation-test dependency audit |
| React | app/features/customer-booking contracts, exact JSON/decimal codec, API adapter, form/quote/confirmation/result UI, focused tests; Customer route; separate test-harness entry and Vite config |
| Flutter | core session/request/exact JSON ports; features/customer/booking models/API/controller/screen; app composition; mirrored tests and separate test_support entry/HTTP smoke; minimal portable HTTP dependency |
| Verification support | Test-only Flutter static/profile browser proxy; actual browser interaction against the loopback Delivery fixture |
| Documentation | Current assessment/results, client run instructions, IAM/zone adapter boundaries and development-secrets guide |

Normal Delivery Development/Production authentication stays closed. All fixture
identities, fixture context and fictional zone/tariff seeding remain in test
infrastructure. No IAM endpoint, signer, token/refresh manager, Admin permission
change, production zone endpoint, service database access or operational tariff
is introduced.

Both client features receive a session snapshot, authenticated-request port and
zone catalog. The normal application compositions provide no fictional session.
The IAM member can supply the real adapters later without rewriting the form,
quote, retry or success logic. The future Customer zone source remains a
separate adapter contract; the isolated fixture supplies IDs from its own DB.

Numeric JSON values are retained before binary floating-point conversion and
written as numbers, preserving large totals and exact submitted weight. Price
and tracking values come from the actual server response. Estimate and decline
do not call parcel creation. Explicit confirmation freezes its owner/key/body;
unknown outcomes retry the same attempt even after local quote expiry. A
definite expiry or price_mismatch requires another estimate and explicit consent.

## Verification results

The backend full regression checkpoint passed **530/530 tests**, with zero
failures/skips: Delivery 439 (7m27s), Gateway 68 (2m22s), Identity 12, Merchant 6
and Notification 5. This includes four new real-socket client-harness tests.
After the runner's console cancellation fix, cached restore of all 12 projects,
the normal Release build (zero warnings/errors, 10.20s), focused harness tests
(4/4, 28s), formatting and EF migration drift verification passed again. The
real five-host foundation smoke passed its dependency audit, Gateway forwarding,
private-listener checks and protected POST 401 checks. Runtime API source and
database migrations did not change.

Runner probes verified a Customer quote/confirm/replay against owned PostgreSQL,
six invalid command-line cases returning usage/exit 2, and a Courier host with
redirected stdin accepting `stop`, exiting 0 and cleaning its database. Ctrl+C
was not directly verified; the documented `stop` command was.

React's full `npm run check` passed: formatting, ESLint with zero warnings,
strict TypeScript, **66/66 tests** across four files and a production SPA build.
No npm dependency or lockfile changed. An independent Chromium run against the
actual Delivery fixture verified form validation, quote-only preview, decline,
rapid duplicate confirmation, server tracking, and recovery after deliberately
discarding a response **after the server committed**. The retry after local
expiry and test-target reload preserved the original key/body and returned the
identical committed response. A 320px viewport had no horizontal overflow; quote,
result and narrow-layout screenshots were visually inspected. No browser script
errors occurred. The normal production route required Customer sign-in and sent
zero fixture or booking requests. Its build contained no test bootstrap markers.

Flutter's final formatter check passed (18 files, zero changes), analysis
reported zero issues (6.1s), and **56/56 tests passed**. The normal release web
build (30.8s) and separate profile fixture build (31.8s) passed, including Wasm
dry runs, with renderer assets bundled locally. Its tracking result has an
explicit accessible server-value label and copy control, with a widget
regression covering the exact clipboard value. An actual app-route widget test
verifies quoted-draft restoration across `/book` push/edit/requote/pop/confirm;
uncertain remount recovery still uses the original key/body. SDK-generated
platform changes containing only line-ending differences were restored.

Independent Chromium interaction with the rebuilt Flutter fixture passed quote,
decline, duplicate-confirm guard and server tracking checks, then recovered an
actual committed booking whose response was deliberately discarded. The retry
returned identical response bytes with the original key/body. There were zero
browser script errors, and the result's accessible tree contained the exact
server tracking value. Quote/form and result screenshots at 430px were visually
inspected. A pure Dart HTTP smoke also created and replayed a real booking. The
latest normal release entry required Customer sign-in, sent zero fixture/POST
requests and had zero browser script errors; its JS/HTML contained no fixture
endpoint, actor, zone or TEST DATA markers. Android XML and the Python proxy's
syntax passed separate checks.

Both features exercise exact large monetary values and decimal weights without
a binary floating-point round trip, stale-response/session changes, safe server
errors, explicit repricing consent and same-attempt recovery. Browser evidence
uses the real Delivery handlers and PostgreSQL behind a server-selected fixture
principal; it does not establish real signed-token or Gateway interoperability.

Remaining acceptance is the IAM member's authenticated session/transport,
approved production Customer address/zone catalog, Android SDK/device execution
and real signed-token/Gateway integration. Flutter retains uncertain attempts in
its app-owned controller across navigation, but not app restart. Normal React
uses an injected in-memory attempt store; reload persistence belongs to the
separate test composition. The owning app must define its production retention
policy. Docker has no running daemon here, so no container or hosted-CI result
is claimed. No live Neon/broker credentials were used.

On 25 October 2026, all owned browser/test listeners were stopped. The tool
interrupted its noninteractive runner process; `pg_ctl status` confirmed its
private server was stopped before its exact owned workspace directory was
removed. Zero private test directories/listeners remained. This process cleanup
is separate from the runner's successfully verified interactive `stop` probe.
Documentation links and the secret guide's two PowerShell blocks passed syntax
checks without executing secret-storage commands. A synthetic Unicode JSON pipe
check verified the guide's explicit UTF-8 setting on Windows PowerShell 5.1.

## Secret storage

See [development-secrets.md](development-secrets.md) for exact project-scoped
PowerShell commands and the six infra inputs. The examples use the existing
UserSecretsId values and pipe private prompted values through JSON, rather than
putting credentials in command history. No actual secret was read or stored by
this implementation. Neither client nor the local booking harness needs Neon,
RabbitMQ or IAM credentials.

## Checkpoint

Phase 6 feature and isolated integration verification are complete. Local client
commits are React **7926824** and Flutter **4d0eecf**, both
`feat(customer): add quote and booking flow`; the backend harness/report are
committed on `test/customer-booking-client-harness`. Docs and infra stayed clean
on their original main commits, and backend main stayed at 32604d4. No push or
main merge was performed.

Stop here. Real signed
Identity interoperability, provisioned Customer address/zone data and Android
release/device acceptance must be recorded separately when available. No Phase
7 Merchant work, push or main merge is included.
