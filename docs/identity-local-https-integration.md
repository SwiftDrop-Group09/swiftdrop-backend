# Owned local HTTPS Identity integration

`integration/IdentityBrowserHarness` is an opt-in executable that exercises real
browser cookies and, optionally, the built React SPA and Flutter native client
against the real Identity API and an owned PostgreSQL instance. It adds no
browser dependency to the default solution or existing test project graph and
does not change production Identity, Gateway, Delivery or infra code.

## Run

Prerequisites are the repository-pinned .NET SDK 10.0.301, installed PostgreSQL
binaries and an installed Microsoft Edge or Google Chrome. The harness defaults
to `msedge`. Set `SWIFTDROP_BROWSER_CHANNEL=chrome` for installed Chrome, or
`chromium` for a separately installed Playwright Chromium. Missing prerequisites
fail with a nonzero exit code; the harness never silently passes or skips them.
It does not install or replace a user's browser.

From the backend root:

```powershell
dotnet restore integration/IdentityBrowserHarness/IdentityBrowserHarness.csproj
dotnet run --project integration/IdentityBrowserHarness/IdentityBrowserHarness.csproj --configuration Release
```

To exercise the actual React UI, build the frontend with its pinned Node/npm and
pass its existing SPA output directory explicitly:

```powershell
dotnet run --project integration/IdentityBrowserHarness/IdentityBrowserHarness.csproj --configuration Release -- --frontend-dist ../swiftdrop-frontend/build/client
```

The Flutter native API/controller smoke is outside default Flutter test
discovery. After `flutter pub get` and the ordinary mobile checks, add its project
directory. The harness keeps its owned server alive, exports only the temporary
**public** TLS certificate, and runs the mobile opt-in test:

```powershell
dotnet run --project integration/IdentityBrowserHarness/IdentityBrowserHarness.csproj --configuration Release -- --frontend-dist ../swiftdrop-frontend/build/client --mobile-project ../swiftdrop-mobile
```

The child command is `flutter test --no-pub integration/identity_api_smoke.dart`
with the owned HTTPS origin and public-certificate path supplied through
`IDENTITY_TEST_ORIGIN` / `IDENTITY_TEST_CERT` dart defines. Its client trusts only
that certificate through `SecurityContext`; it does not use a permissive TLS
callback. Native plugin/device secure storage and actual Google consent are not
being simulated as verified features: this fixture uses an in-memory token
store and an unused provider adapter.

For a Linux CI job without an installed branded browser, explicitly provision
PostgreSQL and Playwright Chromium before this opt-in command. The generated
Playwright script can install Chromium with its OS dependencies:

```powershell
pwsh integration/IdentityBrowserHarness/bin/Release/net10.0/playwright.ps1 install --with-deps chromium
$env:SWIFTDROP_BROWSER_CHANNEL = 'chromium'
```

PostgreSQL must run as an ordinary user on platforms that reject root. Set
`SWIFTDROP_TEST_POSTGRES_BIN` if the binaries are outside normal discovery paths.
No default hosted CI workflow has been changed or claimed successful here.

## What the fixture starts

The existing private PostgreSQL fixture creates a random loopback cluster,
verifies `SHOW data_directory` against its owned absolute directory, creates
only its own Identity database/role and applies the existing migrations. No
environment connection string, live Neon instance or shared PostgreSQL service
is used.

The real `SwiftDrop.Identity.Api.Program` runs through .NET 10
`WebApplicationFactory.UseKestrel(0)` on a real dynamically allocated HTTP socket.
The harness supplies its own signing key, configuration and Data Protection
directory. It neither replaces application authentication nor stubs persistence.

A small **test-only YARP proxy**, using the same YARP 2.3.0 library as Gateway,
terminates real HTTPS and serves the optional existing frontend output. It sets
forwarding headers from the actual connection. Its outbound socket binds
127.0.0.2, the only proxy address trusted by this Identity host; direct 127.0.0.1
callers are therefore genuinely untrusted for forwarding. A second HTTPS fixture
provides different-origin and cross-site attack pages.

The certificate is self-signed for localhost/127.0.0.1 and lasts one hour. The
isolated Playwright browser context accepts this fixture certificate; no system
or browser trust store is modified, and no application TLS policy is weakened.
On Windows, the fixture imports the certificate with DefaultKeySet for SChannel
compatibility and disposes the temporary backing key with the certificate;
PersistKeySet is never used. This proves local HTTPS and cookie behavior, not
the validity of a production CA chain or deployed TLS ingress.

## Checks

The browser checks verify an actual secure context; same-origin bootstrap GET
without Origin after trusted host/protocol forwarding; accepted Secure,
HttpOnly, host-only/path-scoped and SameSite Strict cookies; inaccessible
`document.cookie`; registration, signed login and matching profile identity;
profile PUT with and without CSRF; wrong-Origin rejection even when same-site
cookies are attached; cross-site cookie suppression; unchanged session counts
after attacks; untrusted direct forwarding forgery; proxy replacement of forged
forwarding headers; refresh-cookie rotation without JSON refresh credentials;
logout/family revocation/cookie deletion; and expiry-bound existing access JWTs.

With `--frontend-dist`, Playwright uses the **actual built React forms and
session provider** for Customer registration, login/profile, editing, reload and
rotating restoration, Customer denial of Admin data, persistent-storage checks
and confirmed server logout. No frontend route/API/provider is replaced by a
fixture in that flow. The separate API-level browser probes use a minimal blank
test page so their negative requests cannot interfere with SPA state.

With `--mobile-project`, the actual native Identity API, transport and controller
exercise registration/login, profile writes/reads, body-based rotation, logout,
old-family rejection and reuse-driven sign-out against the same owned server.

The harness logs only named assertions, HTTP method/path/status and browser
version. It disables Playwright debug tracing for its own process, records no
request/response bodies or cookies, and captures child Flutter output without
echoing possible credentials. Test credentials/signing material never become
production assets. Browser contexts, Kestrel hosts, the private cluster, owned
key directories and the transient certificate are disposed on success/failure.
Recursive cleanup checks the exact absolute parent and generated GUID name.

## Observed results

On 27 October 2026, pinned .NET SDK 10.0.301 and installed Microsoft Edge
156.0.4314.42 ran the local fixture. The API/browser-only run passed **20** named
assertions. The run with the actual built React SPA passed **27** named
assertions. The combined command above passed **28/28** named assertions:
20 API/browser checks, seven actual React UI checks and the standalone Flutter
native API/controller test. The Flutter child exited zero and reported all
tests passed. These are opt-in integration results in addition to the ordinary
solution and Flutter unit/widget suites.

Release build completed with **0 warnings and 0 errors**. Scoped formatting
verification and `git diff --check` passed. The harness package vulnerability
scan, including transitive packages, reported **no vulnerable packages** from
NuGet. The default solution, its packages and project allowlist are unchanged.

During setup, standalone WAF content-root/client-base-address mistakes and a
Windows SChannel ephemeral-certificate problem failed explicitly and were
corrected inside the fixture. An initial React navigation timeout was a harness
race: it filled the prior form before the login route rendered. The fixture now
waits for the actual login heading before entering credentials. No production
application change or TLS/cookie weakening was used to make these checks pass.

This remains an opt-in local integration proof. It does not enable the real
production Gateway/ingress topology, Delivery JWT activation, operational JWKS
cache/refresh, live Google accounts, live Neon, native platform/plugin behavior,
containers or hosted CI. Those dependencies retain their separate acceptance
requirements.

References: [.NET 10 WebApplicationFactory source](https://github.com/dotnet/aspnetcore/blob/v10.0.9/src/Mvc/Mvc.Testing/src/WebApplicationFactory.cs),
[Playwright .NET browser channels](https://playwright.dev/dotnet/docs/browsers),
[YARP forwarding transforms](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/transforms-request).
