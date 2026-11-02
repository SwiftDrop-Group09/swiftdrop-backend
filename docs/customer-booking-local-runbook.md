# Run Customer booking without waiting for IAM

The React and Flutter booking features use injected session, request and zone
adapters. Normal applications require a real Customer session. The separate
local test targets below exercise the same features against actual Delivery
handlers and disposable PostgreSQL, with no IAM/Neon/RabbitMQ secrets.

## Start the isolated HTTP backend

Install the pinned .NET SDK and PostgreSQL binaries used by the existing tests.
From the backend root:

```powershell
dotnet build SwiftDrop.sln --configuration Release
$env:SWIFTDROP_TEST_POSTGRES_TEMP_ROOT = 'D:\VS code\swiftdrop'
dotnet run --project tests/SwiftDrop.Delivery.Harness --configuration Release --no-build --no-restore -- --port 5088 --actor customer-a
```

The runner prints http://127.0.0.1:5088/. Keep it running while using either
client. Enter `stop` in its console to shut down and clean up the owned cluster.
It never uses the external test-database fallback or normal service secrets.
SWIFTDROP_TEST_POSTGRES_BIN can select an installed PostgreSQL bin directory.

Actors are selected only at startup: customer-a, customer-b or courier. A/B have
the Customer role; Courier requests are forbidden. Each runner invocation owns
a fresh fixture. Automated harness tests additionally start multiple actors
against the same owned database to verify foreign-quote/replay denial.

Only this test host provides GET /__test/booking-context, returning its fixed
actor, roles and seeded fictional zone labels/IDs. There is no such route or
fake authentication registration in normal Development/Production API hosts.
The private DB includes test tariffs; both clients obtain prices and tracking
numbers from the real server, rather than hardcoding them.

## React

From swiftdrop-frontend, use its .nvmrc Node/npm versions and existing lockfile:

```powershell
npm ci
npm run dev:booking-test
```

Open http://127.0.0.1:5174/. The separate Vite target proxies /api and /__test
to the loopback fixture, with an explicit fictional-data banner. Its optional
BOOKING_TEST_TARGET must remain loopback. It is not a production Router route.

The ordinary app route is /customer/book. Without the future IAM/application
adapter it shows Customer sign-in required and sends no booking or fixture
requests. A production build contains no fixture bootstrap or tab storage.

The test target retains only validated owner/quote/amount/key/body metadata in
tab sessionStorage, enabling same-attempt recovery across reload. It stores no
tokens, addresses or recipient details. Normal feature composition uses an
injected in-memory attempt store unless the owning app supplies another policy.

## Flutter browser fixture

From swiftdrop-mobile, with its compatible Flutter/Dart SDK:

```powershell
flutter pub get
flutter build web --profile --no-web-resources-cdn -t test_support/booking_harness.dart --output build/booking-harness
python test_support/serve_web_harness.py
```

Open http://127.0.0.1:5181/. Python 3 is needed only for this test-only static
server/proxy. Renderer assets are bundled locally; /api and /__test are proxied
to 5088 on the same origin, without broad CORS or browser-security exceptions.
The fixture target displays TEST DATA and refuses to run in release mode.

The ordinary app entry is lib/main.dart and its /book route. It has no fictional
session and never imports test_support. A normal release build is separate:

```powershell
flutter build web --release --no-web-resources-cdn
```

Flutter retains an uncertain confirmation in its app-owned controller across
screen navigation/remount. It does not persist that attempt across app restart.
The future owning app must supply its reviewed retention/reconciliation policy;
do not interpret closing the app or aborting HTTP as cancellation of a booking.

## Flutter native/HTTP checks

The same API/codec can be exercised without an Android SDK:

```powershell
dart run test_support/booking_http_smoke.dart http://127.0.0.1:5088
```

With Android SDK tools and a device/emulator available:

```powershell
adb reverse tcp:5088 tcp:5088
flutter run -t test_support/booking_harness.dart --dart-define=API_BASE_URL=http://127.0.0.1:5088
```

Only the Android debug network policy permits loopback cleartext. Main has the
ordinary INTERNET permission for future HTTPS transport; release-wide cleartext
is not enabled. Android API 26 remains the minimum. Android build/device checks
were not available in this execution and are separate acceptance evidence.

## Flow and IAM handoff

Fill both addresses, select fixture areas, provide recipient/package details,
request a quote, then explicitly accept it. Declining or editing an unsubmitted
quote creates no parcel. An uncertain confirmation must keep the same owner,
key and exact body, even if its local quote timer expires. Do not replace that
attempt with a new quote/key until its outcome is resolved. A definite expiry
or price_mismatch requires another estimate and explicit acceptance.

The IAM member supplies the real session/authenticated-request adapters later.
The production address/zone source also needs an approved adapter; no Admin
lookup permission was relaxed. See the [implementation results](delivery-phase-6-implementation.md)
and [Identity contract](delivery-identity-integration-handoff.md).
For actual project settings, use [the secret-storage commands](development-secrets.md).
No secret is needed for this isolated fixture.
