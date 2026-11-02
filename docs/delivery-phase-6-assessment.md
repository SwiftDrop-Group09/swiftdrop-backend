# Delivery Phase 6: original Customer client assessment

## Current follow-up

The user subsequently approved continuing Phase 6 independently of the IAM
member, using replaceable client adapters and a separate test-only HTTP harness.
The booking features and isolated integration harness are implemented; current
code scope and executed results are in
[the Phase 6 implementation report](delivery-phase-6-implementation.md).
The assessment below records the earlier documentation-only checkpoint at
5ee5420. Its missing-tool and missing-feature observations are historical, not
the current follow-up's verification result.

## Historical checkpoint status at 5ee5420

**Client implementation is blocked; the Phase 6 completion gate is not met.**
Both Customer clients must still implement and demonstrate the booking flow.
This execution supplies the original master's explicit fallback: a verified,
contract-based handoff when shared application infrastructure is unfinished.
No Customer screens or passing client feature tests are claimed.

The original Phase 6 instruction states: "If frontend implementation is owned
by another teammate or is blocked by unrelated unfinished application
infrastructure, do not make broad unapproved changes. Prepare a contract-based
handoff and report the exact blocker." The observed condition is unfinished
infrastructure, not an assumed allocation of the assigned Customer feature.

## Actual baseline and before-change verification

All five repositories were clean before edits:

| Repository | Inspected branch / commit | Actual state |
|---|---|---|
| Backend | fix/delivery-authentication-boundary / d814089 | Phases 1-5 present; main remains 32604d4 |
| Frontend | main / c062b11 | Blank React entry route with tooling recorded in the lockfile, no product features |
| Mobile | main / e9778c1 | Flutter counter scaffold |
| Docs | main / d97fff4 | Current local contracts; security/design choices remain Proposed |
| Infra | main / 075a8b4 | Shared runner/Compose; no client session infrastructure |

The original master, backend/frontend contribution instructions, READMEs,
source/configuration/dependency/test inventories, prior implementations and
current REST, security, booking sequence, requirements and deployment contracts
were reinspected. No AGENTS.md was found. Existing plans and example directories
were checked against real source rather than treated as implemented features.

Before any edits, the backend checkpoint was rerun on d814089:

- Cached restore passed for all 11 projects.
- Release build passed with zero warnings/errors (10.67s).
- **526/526 tests passed**, zero failed/skipped: Delivery 435 (7m24s), Gateway 68
  (2m22s), Identity 12, Merchant 6, Notification 5.
- EF reported no pending model changes; Delivery formatting verification passed.
- Real five-host/Gateway smoke passed, including protected quote/booking POST
  401 despite forged identity, absent feature probes and private listener checks.
  All hosts started by the script stopped.
- No private PostgreSQL fixture directories remained; Git stayed clean.

The new branch `docs/delivery-customer-client-handoff` starts from d814089,
which already contains the preceding phase commits. No merge or history rewrite
is necessary. Main and the other four repositories remain unchanged.

## Exact blockers

| Dependency | Evidence / missing implementation | Required unblocker |
|---|---|---|
| React Customer session and authenticated client | app/portals/auth, app/portals/customer, app/features and app/shared contain only .gitkeep; public index returns null. app/root.tsx and providers.tsx provide routing/Query only. No auth session, token-aware transport or Customer shell exists. | Supply the shared Customer session, route/guard integration and API-client interfaces. Booking must consume them without another token/refresh manager. |
| Flutter shared app foundations | lib/main.dart is the generated counter; pubspec has Flutter/Cupertino only. lib/README.md's app/core/features tree is illustrative, and lines 68-70 leave navigation, state and HTTP choices unresolved. API_BASE_URL is not read. | Establish the shared app/router/state and authenticated network/session/base-URL interfaces; retain the existing app and Courier boundary. |
| Actual Identity integration | Identity implements registration, health and readiness, without login/token issuance/JWKS. Delivery's normal environments use UnconfiguredIdentityHandler and return 401. | Complete the [Identity handoff](delivery-identity-integration-handoff.md) and provide a real authenticated client session. Backend test principals stay test-only. |
| Customer address/zone source | Both addresses require zoneId. Docs api/rest-api.md:44 makes GET /api/v1/zones Admin-only; it is not implemented. There is no Customer zone catalog/address resolver or operational seed. | Agree and supply an authorized zone-bearing address/catalog source. Do not widen Admin access, invent coverage/UUIDs or use raw technical IDs as ordinary Customer input. |
| Runnable React tools | .nvmrc pins Node 24.16.0; installed Node is 24.9.0, below package engines >=24.16.0 <25. npm.cmd is 11.14.1 versus packageManager 11.13.0 (within the declared npm engine range). node_modules is absent. | Use the pinned tools and existing lockfile with npm ci before executing client checks; no dependency versions were changed here. |
| Runnable Flutter tools | flutter --version and dart --version fail with CommandNotFound. No executable was found on PATH or the usual checked SDK locations. pubspec requires Dart ^3.13.2. | Provision a compatible Flutter/Dart SDK and the Android toolchain; then run format, analyze, widget and device checks. |

These prevent a verified integration using an established client session/API
client. Creating replacement shared authentication, choosing Flutter-wide
infrastructure or altering Admin/API contracts would exceed this phase's
fallback. Tool installation alone would not resolve the application/zone gaps.
The absent Customer route, forms and result screen are the assigned feature
work; their absence alone is not treated as a blocker. The missing shared
session/client integration and authorized zone source are the dependencies.
No named owner, teammate confirmation or additional architectural approval is
invented; the responsibility register remains Unassigned/Pending.

## Contract decisions and source contradictions

The [Customer client handoff](delivery-customer-clients-handoff.md) specifies
the exact request/response shapes, usability validation, explicit confirmation,
retry state, decimal handling and pending test matrix for both clients.

The current backend agrees with the booking REST contract. Important limits:

- Quotes return total/currency/version/expiry/fingerprint, without a fee
  breakdown. Clients must not calculate or invent missing fee components.
- Booking returns a committed six-field projection and Location; the tracking
  GET behind that Location is not implemented. Display the returned tracking
  number without manufacturing a successful tracking read.
- Both addresses need valid eligible zones; deliveryAddress.zoneId selects the
  tariff. Pickup eligibility remains independent. No geographic mapping or
  cross-zone surcharge is defined.
- An uncertain confirmation must retry its original owner/key/body even after
  local expiry. The backend replays a committed result before quote checks.
- Numeric JSON decimals must survive client decoding/encoding exactly. Both
  JavaScript Number and Dart double can lose valid monetary values; converting
  an already rounded double to decimal cannot recover the original amount.

Frontend README still says backend feature handlers are absent; actual d814089
and the executed tests supersede that status text. Mobile README mentions old
SignalR/telemetry language and LAN setup; current docs defer SignalR/GPS and
require the documented debug-only loopback/TLS policy. Those unrelated client
documents and platform settings were not rewritten. Existing operational and
Identity ambiguities remain recorded in the Phase 4/5 assessments.

## File scope and results

The file-level plan, followed after the prerequisite gate, is documentation only:

1. Add docs/delivery-phase-6-assessment.md with actual baselines, blockers and results.
2. Add docs/delivery-customer-clients-handoff.md with the verified integration contract and pending client acceptance.
3. Link both documents from the backend README.

No React/Flutter source, packages, lockfiles, auth, Courier flows, backend runtime,
wire assets, routes, database model/migrations, infrastructure or shared docs change.

| Check | Observed result |
|---|---|
| Backend restore/build/full test, EF, formatting and actual-host smoke | Passed before edits as recorded above; source unchanged, so the full suite is not repeated for Markdown-only changes |
| Existing React test command | npm.cmd run test:ci exited 1 because vitest was not found; dependencies are absent. The test runner did not execute any tests. |
| React full check/build | Not executed; pinned Node/dependencies are unavailable. No client code changed. |
| Flutter version/Dart version | Commands unavailable; no SDK result |
| Flutter format/analyze/test/build/device acceptance | Not executed; SDK unavailable and client foundations absent |
| Customer feature tests / real cross-client booking | Not implemented or executed; pending matrix in the handoff |
| Final document/link/diff review and repository cleanup | Passed: all local links in the new documents resolve; git diff --check clean; independent React/Flutter reviews found no material issue; zero private PostgreSQL fixture directories; the other four repos remain clean at their starting commits and main remains 32604d4 |
| Wire compatibility | SHA-256 matches for StatusChanged schema, delivery.proto and route fixture against current docs; wire assets unchanged |
| Live Identity, Neon, broker, Docker or hosted CI | Not executed in this documentation-only checkpoint; no deployment result claimed |

Backend commands, executed from the backend root before Markdown edits:

```powershell
dotnet restore SwiftDrop.sln --source 'C:\Users\adith\.nuget\packages' -p:NuGetAudit=false
dotnet build SwiftDrop.sln --no-restore --configuration Release
$env:SWIFTDROP_TEST_POSTGRES_TEMP_ROOT = 'D:\VS code\swiftdrop'
dotnet test SwiftDrop.sln --no-build --no-restore --configuration Release --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'
dotnet-ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
dotnet format SwiftDrop.sln --no-restore --include src/Services/Delivery/SwiftDrop.Delivery.Api tests/SwiftDrop.Delivery.Tests --verify-no-changes --verbosity minimal
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-foundation.ps1 -Configuration Release
```

The installed EF executable was used without local-tool restore. Client tool
checks were node --version, npm.cmd --version and npm.cmd run test:ci from the
frontend root, and flutter --version / dart --version from the mobile root.
The test runner/SDK failures above are not executed feature-test results.
After edits, git diff --check, local link resolution, source/wire compatibility,
repository status and fixture cleanup checks passed. No online vulnerability
audit is claimed. PostgreSQL was disposable test infrastructure, not shared Neon.

## Stop and resume boundary

Commit the scoped handoff locally as
`docs(delivery): hand off customer client integration prerequisites`.
No push, merge, Phase 7 or broad client bootstrap is included.

Resume **Phase 6**, not Phase 7, once the session/client/zone interfaces and
matching tools are available. Then implement both feature slices, execute their
focused and repository checks, and demonstrate real authenticated booking on
both clients before marking the Phase 6 completion gate passed.
