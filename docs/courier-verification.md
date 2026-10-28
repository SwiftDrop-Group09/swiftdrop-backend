# Courier verification runbook

This is the Phase 8 verification entry point, not a deployment approval. Use the
existing feature branches; do not merge independent Booking, Identity or tracking
work as a side effect of running checks. Production Delivery currently fails
closed for human JWTs. Passing fixture checks does not change that boundary.

## Prerequisites and ordinary checks

Use the repository-pinned .NET SDK, Node/npm and compatible Flutter/Dart SDK.
Keep backend, frontend and mobile sibling checkouts named `swiftdrop-backend`,
`swiftdrop-frontend`, `swiftdrop-mobile`. Install PostgreSQL binaries for the owned
test fixture. Tests never need a guessed shared database password.

From backend:

```powershell
dotnet restore SwiftDrop.sln
dotnet build SwiftDrop.sln --no-restore --configuration Release
# Choose an ignored, writable, absolute directory for fixture-owned clusters.
$env:SWIFTDROP_TEST_POSTGRES_TEMP_ROOT = Join-Path $pwd 'artifacts/courier-phase8-postgres'
Remove-Item Env:\SWIFTDROP_TEST_DELIVERY_DATABASE -ErrorAction SilentlyContinue
dotnet test SwiftDrop.sln --no-build --configuration Release --logger 'trx;LogFilePrefix=courier-phase8-final' --results-directory artifacts/courier-phase8-final
./scripts/verify-foundation.ps1 -Configuration Release
dotnet ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
dotnet ef migrations script --idempotent --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --output artifacts/courier-phase8-migrations.sql
dotnet list SwiftDrop.sln package --vulnerable --include-transitive --format json --no-restore
```

EF tooling must match the project's 10.0.9 package line. SQL generation/model
inspection is not permission to apply migrations to shared Neon. Compare the
checked-in event schema to `swiftdrop-docs/api/contracts/status-changed-v1.schema.json`.
The integration tests validate actual stored events against that schema.

From frontend: `npm ci`, `npm run check`, `npm audit --json`.
From mobile: `flutter pub get --enforce-lockfile`, `flutter analyze --no-pub`,
`flutter test --no-pub`, `flutter build web --release --no-pub` and the applicable
native build/device checks. Report each unavailable target separately.

## Both clients against one owned fixture

The Windows PowerShell runner starts the existing Release harness on loopback,
runs the production React and Flutter API adapters in their explicit test entries,
then inspects the real PostgreSQL state. It always disposes its owned harness.
It uses the existing fixed-principal fixture and memory proof adapter, not live
JWTs or a private bucket. No test route is added to normal services.

```powershell
./scripts/verify-courier-clients.ps1
```

`dotnet`, pinned `node` and `dart.bat` must be on this process's PATH. Port 5088
must be free. The runner passes the reported loopback address to both clients,
uses actual child exit codes, and captures informational Dart stderr as a log
instead of treating it as a PowerShell test failure.

For Chrome, first start the existing frontend dev server on 127.0.0.1:5173 with
`DEV_GATEWAY_TARGET=http://127.0.0.1:5088`. Have Playwright and Chrome installed;
the existing `PLAYWRIGHT_MODULE` override may identify a file URL to an installed
Playwright module. Then run `./scripts/verify-courier-clients.ps1 -Browser`.
Browser Identity responses are explicitly stubbed by that test script. Courier
HTTP is forwarded to the real owned Delivery fixture. Screenshots contain only
fictional data. Browser mode uses port 5088; HTTP-only mode may select `-Port`.

The runner requires this fixed fixture's final state: two Delivered and three
Failed parcels; no active assignments; photo and signature references; unchanged
price snapshots; one history/event per parcel version; 27 history/outbox rows;
10 retained replay rows; exact v1 event schema with no private proof/notes; no
publication without a relay. Counts are test dataset expectations, not business
limits. The new regression verifies incomplete work fails the gate, complete
work passes without writes, and the existing PostgreSQL unique constraint rejects
a duplicate outbox event.

Logs and `results.json` are under `artifacts/courier-phase8-clients`.
`completed=true` covers only the explicitly named fixture mode. Its
`liveJwtVerified` and `liveObjectStorageVerified` fields remain false.
An omitted browser run is `not-requested`, not passed.

## Live private-storage acceptance — explicit opt-in

The ordinary unit suite uses SDK doubles. To test a real provisioned dedicated
test bucket, configure the existing `ProofStorage__Endpoint`, `__Bucket`,
`__Region`, `__AccessKey`, `__SecretKey`, and optional `__SessionToken` in the
process environment. Also set `SWIFTDROP_COURIER_ACCEPTANCE_BUCKET` to exactly
that dedicated test bucket's name. Supply its reviewed private/encrypted setup
from the [storage runbook](courier-proof-storage.md); do not use a production
customer-data bucket. No credential values belong in committed commands or logs.

```powershell
dotnet tests/SwiftDrop.Delivery.Harness/bin/Release/net10.0/SwiftDrop.Delivery.Harness.dll --verify-private-storage
```

Exit 3 means blocked (missing configuration or dedicated-bucket confirmation).
Exit 1 means failed; exit 0 requires every check and cleanup to succeed.
The command never loads ambient AWS credentials, user secrets or creates a bucket,
policy or permission. It uses only a generated invocation-owned `proofs/` key.
It verifies conditional creation, private ACL/policy, requested/read-back AES256,
MIME/size/hash/bytes, duplicate-write rejection, list visibility, anonymous byte
denial and deletion of that key. Endpoint HTTPS and certificate validation are
preserved; only the existing loopback HTTP exception remains.

On an ambiguous upload outcome cleanup attempts only that generated key. A
cleanup failure makes the check fail, and may require the bucket owner to run
the reference-aware orphan cleanup when storage becomes available. This command
does not establish production KMS/IAM/TLS/retention or authorized customer streaming.
No unchecked default bucket, public access fallback or encryption downgrade exists.

## Acceptance boundary

Final release acceptance additionally needs real Courier JWT issuance and
Delivery verification with agreed issuer/audience/trusted JWKS, provisioning,
revocation-bound checks, Flutter IAM multipart wiring, native device/camera/cache
checks, encrypted live bucket acceptance, authorized customer/admin proof download,
reviewed Neon rollout/grants/TLS, Linux container decoding and outbox relay/
notification delivery. Current branches/configuration do not supply all of these.
Do not mark those checks passed from fixed principals, memory storage, absent
routes, health endpoints or a persisted-but-unpublished outbox.

Phase 8 is the final numbered phase in the master prompt. Resolve the documented
owner/environment dependencies and rerun its live acceptance gate; do not invent
Phase 9 or automatically commit, merge, deploy or publish.
