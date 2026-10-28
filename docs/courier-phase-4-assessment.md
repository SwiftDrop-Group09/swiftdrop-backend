# Courier Phase 4: private proof and completion

Scope: Phase 4 only, FR-R03 and FR-R02's final completion guard. No dispatcher,
failure-client, history, receipt, React or Flutter work. No commit/push/merge.
Implemented and verified across 28-29 October 2026. The backend application
checkpoint is complete under isolated trusted principals and storage fixtures;
live deployment acceptance remains subject to the explicit dependencies below.

## Inspection before implementation

At the start, backend was feat/courier-workflow-persistence at 4723578. The 31 existing
uncommitted Phase 3 files are preserved; hashes were recorded in ignored
artifacts/courier-phase4-starting-files.json. Their TRX reports actually contain
834 passes, zero failures/skips (743 Delivery, 68 Gateway, 12 Identity, 6 Merchant,
5 Notification). Phase 3 source, transaction, idempotency migration and status
guard have been re-read. No later developer commit or unrelated working-tree
change was found. Docs/main d97fff4, infra/main 075a8b4, frontend/main c062b11 and
mobile/main b819b42 remain clean. No AGENTS.md applies. CONTRIBUTING applies.

Read the original master prompt Phase 4, current SRS/traceability/use cases,
REST ProofInput/CompleteDeliveryWithProof, state matrix, authorization/data
protection, data architecture, ER storage section, assignment-proof sequence,
TC-R03, event schema and deployment/storage guidance. They are still Proposed;
no architectural approval is inferred. User previously explicitly approved
photo OR signature, with both accepted, and the retained assignment lifecycle.

Verified contracts:

- POST /api/v1/deliveries/{id}/pod, multipart photo and/or signature, recipientName
  (nonblank, at most 120), expectedVersion >=1 and Idempotency-Key. Human Courier
  and current assignment only; no caller actor/object reference is accepted.
- JPEG/PNG decoded content, at most 5 MiB each / 10 MiB combined; server-generated
  private keys, encrypted MinIO locally / S3 in cloud (ER storage section).
- Only OutForDelivery -> Delivered, after confirmed private object persistence;
  assignment/version must still match at commit. Status PUT stays unable to deliver.
- Proof metadata, parcel/version, Delivered assignment, history, unchanged
  StatusChanged v1 and exact 200 replay must commit together. Fingerprint binds
  original content hashes, parcel, expectedVersion and recipientName.
- Stage before the final transaction. Failed DB writes leave cleanup candidates;
  daily cleanup deletes unreferenced objects older than 24 hours and protects
  committed references. No database/object-store distributed transaction is claimed.

Existing ProofMetadata/Photo/Signature, retained assignments, history, outbox,
current-user adapter and PostgreSQL transaction machinery are reused. No storage
adapter/credential or live Identity verification currently exists. Production stays
fail-closed; isolated HTTP tests use the existing test-only principal fixture.

## File-level implementation plan

- Application/CourierProof: multipart-independent command, content-hash validation,
  proof-completion handler, storage/image ports and cleanup orchestration.
- Domain: focused proof-validated Parcel completion, completion history/outbox
  factories and focused 200 proof replay method; do not extend status PUT's policy.
- Reuse PostgresCourierStatusStore's parcel-first locks, fresh assignment recheck,
  reservation and commit; add a proof transaction port and tracked metadata save.
  Same-actor Delivered receipt replay mirrors Phase 3's terminal retry rule without
  granting historical detail access. Preflight replay/state checks precede upload;
  final transaction rechecks everything after upload.
- Infrastructure/CourierProof: bounded decoded-content validation and metadata-free
  image re-encoding; private S3/MinIO adapter with explicit Delivery credentials,
  TLS outside loopback, no redirects/public URLs, conditional create, encryption
  and read-back verification. No local public filesystem fallback.
- Daily cleanup with PostgreSQL object advisory locks shared with completion,
  fresh reference checks and fail-closed storage/database handling. No unguarded
  bucket lifecycle deletion of committed images or ambiguous-commit eager deletion.
- Presentation/CourierProofEndpoints: exact existing multipart route, bounded
  streaming parser, strict fields, safe errors/private cache; minimal Program DI.
- New constraint-only migration permits 200 for CompleteDeliveryWithProof while
  retaining status and Booking receipts; existing migrations/triggers unchanged.
- Focused domain/security/HTTP/PostgreSQL/reliability/storage/cleanup/migration
  tests, complete solution regression, EF model/script checks and host smoke.
- README and infrastructure/storage runbook with exact configuration, rollout,
  operational bounds, evidence, mismatches and remaining live dependencies.

Image decoding needs a maintained cross-platform codec. Package/security and
official API documentation are checked before adding dependencies. Technical
resource limits protect decoding/multipart processing; they are documented as
server safeguards rather than invented business proof types. DownloadProof and
receipts are separate Customer operations, not added by this upload/completion
checkpoint; no public or Courier download route is introduced.

## Results

During implementation an external commit d663387 appeared on this same branch.
It contains Phase 3 plus this initial Phase 4 assessment and the three initial
package references. The agent did not create/stage it. Its contents were inspected
and preserved; all later Phase 4 feature edits remain in the working tree. The
starting-file hash inventory distinguishes the Phase 4 delta from existing work.

## Implemented functionality and API/database changes

The exact documented multipart completion route is implemented. Photo alone,
signature alone and both together are accepted. Server-generated private keys,
full pixel decoding, input byte/MIME/size checks and fresh metadata-free encoding
prevent filename/magic-header trust, arbitrary references and source EXIF/GPS
storage. The original content hashes bind canonical replay alongside parcel,
recipientName and expectedVersion. There is no public upload URL or client upload
completion flag. HTTP parsing is bounded and rejects unknown/duplicate fields,
malformed/truncated input, unsupported MIME, invalid name/version and missing proof.

Authentication/role checks precede body processing; trusted actor and actual current
assignment are checked again by the handler. A short preflight transaction checks
ownership, replay, state and version before decoding/storage, then rolls back its
reservation. After storage succeeds, a new transaction repeats those checks under
parcel-first then assignment locks and actual EF Version/xmin predicates. Shared
object advisory locks protect read-back verification and metadata commit from
cleanup. The staging-age check occurs after verification, preventing an upload
that expires during a storage call from committing.

Only OutForDelivery -> Delivered succeeds through this proof operation. The status
PUT's transition matrix remains unchanged and still rejects Delivered. Completion
atomically writes ProofMetadata, incremented parcel/version, Delivered assignment
and timestamp, append-only private history, unchanged ten-field StatusChanged v1
outbox and exact 200 replay receipt. No broker publish or cross-system transaction
is claimed. The manifest removes the closed assignment. The original Courier can
replay their own terminal proof receipt and earlier status receipts without storage
access or another event; this grants no historical details or proof download.

The response uses the established six common Parcel fields and immutable price
snapshot. It contains no recipientName, actor ID, object key, proof ID or image
bytes; the event also excludes proof/private metadata. Existing Customer Booking,
tracking, quote consumption and 201 replay, Phase 2 reads and Phase 3 state/failure
guards are preserved. The shared status store/codec/event factory are reused rather
than creating a second outbox or transaction system.

Database migration: 20261028175800_CourierProofCompletion. It extends only the
existing replay completion constraint for operation CompleteDeliveryWithProof 200,
preserving all valid prior Booking 201/status 200 envelopes. It also adds the
proof_metadata_retained UPDATE/DELETE rejection trigger; EF provides the matching
guard. Committed proof references remain immutable and retained. No new table,
column, parcel/assignment state or event schema is introduced. No previous migration
is rewritten, no normal-startup migration is enabled, and no shared Neon DDL is run.
Downgrade to Phase 3 safely fails if proof 200 receipts exist; the transaction rolls
back and retains proof/receipt data. Retain the compatible schema during code
rollback or use a reviewed forward migration; do not delete retained records.

## Storage, cleanup, security and concurrency

S3ProofStorage is the actual MinIO/S3 SDK adapter, with explicit Delivery-only
credentials, safe configured origin, TLS outside loopback, disabled redirects,
conditional create (If-None-Match:*), AES256 server-side encryption and full
read-back SHA-256/MIME/length/encryption checks. A linked 30-second operation deadline
also covers streamed reads; caller cancellation propagates, while provider/client/
timeout errors become safe 503 responses. There is no ambient credential search,
public ACL, storage URL or local filesystem fallback. ACL must be owner-only;
resource policies may be deny-only, with access granted by the Delivery identity
policy. Unsupported/unreadable/private-policy checks fail closed rather than
assuming an unconfigured bucket is safe.

ProofCleanupWorker runs at startup and daily when storage is configured. It considers
only generated proofs/ keys older than 24 hours. Each deletion holds an exclusive
nonblocking object advisory lock and checks committed metadata in a fresh database
statement. Busy or referenced objects are retained; completion holds the matching
shared locks through commit. The store requires these object locks before saving
proof metadata. Database failure never bypasses reference checks. Failed passes
may have already removed earlier confirmed orphans; remaining candidates retry
on a later pass. No unconditional bucket lifecycle is enabled.

Database failures, cancellation, second-file failure, duplicate-request losers and
lost acknowledgement may leave staged objects. No eager deletion is attempted
after an uncertain commit; reference-aware cleanup protects any committed proof.
There is no claim that object storage and PostgreSQL share a transaction. The
storage port exposes no public download capability. Direct anonymous/shared bucket
permission enforcement must also be verified in the actual deployment, not inferred
from a mock SDK response.

Technical server safeguards, documented in the storage runbook: 10 MiB combined
input plus 64 KiB multipart framing, 4 KiB section headers, 1 KiB text fields,
single-frame images up to 16,777,216 pixels / 64 MiB RGBA and two concurrent decodes.
These allocation/parser budgets are separate from the approved proof business
policy. Original filenames are ignored. Re-encoding removes source metadata and
may change stored size; the 5 MiB limit applies to each original upload. Native
decoding is bounded and checked before allocating pixel buffers.

Production Identity authentication remains fail-closed, unchanged. Test-only trusted
principals are supplied only by the existing fixture. Test factories and the smoke
runner now explicitly clear proof bucket configuration so private environment/user
secrets cannot start live cleanup during offline tests. No private setting or
credential value was printed or committed by the agent.

## Files changed

Phase 4 touches 40 backend files relative to its starting state (24 added, 16
extended), including the initial assessment/package references captured by the
external commit. Other repositories are unchanged. The full inventory is also
recorded locally in ignored artifacts/courier-phase4-files.json. Paths in the next
table are relative to src/Services/Delivery/SwiftDrop.Delivery.Api/.

| Files | Change |
|---|---|
| Application/CourierProof/ProofContracts.cs (new) | Input/storage/transaction/cleanup ports and private proof records |
| Application/CourierProof/ProofValidator.cs (new) | Required metadata, original content hashes and canonical fingerprint |
| Application/CourierProof/CourierProofHandler.cs (new) | Preflight, staged upload, final proof-bound completion and exact replay |
| Application/CourierProof/ProofCleanup.cs (new) | Reference-aware orphan cleanup orchestration |
| Application/CourierStatus/CourierStatusHandler.cs | Retained same-actor status replay after Delivered closure |
| Domain/ProofCompletionPolicy.cs (new), Domain/Parcel.cs | Required distinct parcel-bound photo/signature and versioned completion |
| Domain/BookingHistory.cs, Domain/OutboxMessage.cs | Completion factories using existing entities and unchanged event schema |
| Domain/IdempotencyRecord.cs | Focused CompleteDeliveryWithProof 200 result; original methods retained |
| Infrastructure/CourierProof/SkiaProofImageValidator.cs (new) | Bounded native decode and metadata-free encoding |
| Infrastructure/CourierProof/S3ProofStorage.cs (new) | Private encrypted conditional S3/MinIO staging, verification, listing and deletion |
| Infrastructure/CourierProof/ProofObjectKeys.cs (new) | Generated-key validation and stable advisory lock identity |
| Infrastructure/CourierProof/PostgresProofCleanupStore.cs (new) | Exclusive object lock, fresh reference check and guarded deletion |
| Infrastructure/CourierProof/ProofCleanupWorker.cs (new) | Configured startup/daily worker with safe failure logging |
| Infrastructure/CourierStatus/PostgresCourierStatusStore.cs | Reused locking/reservation/commit via proof port; tracked metadata and object locks |
| Infrastructure/Persistence/DeliveryDbContext.cs | Reject committed proof changes/deletions |
| Infrastructure/Persistence/DeliveryEntityConfigurations.cs | Compatible proof 200 replay completion constraint |
| Infrastructure/Persistence/Migrations/20261028175800_CourierProofCompletion.cs and .Designer.cs (new), DeliveryDbContextModelSnapshot.cs | Additive constraint/retention migration and matching model |
| Presentation/CourierProofEndpoints.cs (new) | Exact multipart route, bounded parser, strict input and safe errors |
| Program.cs | Scoped proof/cleanup ports, image/storage singleton, worker, route and private/auth metadata |
| SwiftDrop.Delivery.Api.csproj | Pin AWSSDK.S3 4.0.104.7, SkiaSharp and Linux.NoDependencies 4.154.0 |
| Infrastructure/README.md | Completion/migration/cleanup integration handoff |

Other backend-relative files:

| Files | Change |
|---|---|
| tests/SwiftDrop.Delivery.Tests/Courier/CourierProofApiTests.cs (new) | Real PostgreSQL HTTP proof variants, ownership, replay, storage faults and races |
| tests/SwiftDrop.Delivery.Tests/Courier/CourierProofCleanupTests.cs (new) | Old-orphan removal, committed/busy protection and database failure |
| tests/SwiftDrop.Delivery.Tests/Courier/CourierProofDomainTests.cs (new) | Required, distinct, parcel-bound proof and terminal/version guards |
| tests/SwiftDrop.Delivery.Tests/Courier/CourierProofImageTests.cs (new) | Real JPEG/PNG decode, malformed/spoofed/unbounded input, metadata stripping and fingerprints |
| tests/SwiftDrop.Delivery.Tests/Courier/CourierProofMigrationTests.cs (new) | Existing status receipts survive upgrade/completion; retained proof and safe downgrade |
| tests/SwiftDrop.Delivery.Tests/Courier/CourierProofReliabilityTests.cs (new) | Seven database fault points, second-file failure, cancellation and commit acknowledgement loss |
| tests/SwiftDrop.Delivery.Tests/Courier/CourierProofSecurityTests.cs (new) | Real-host auth/role/subject, forged fields, size/MIME/multipart/input rejection |
| tests/SwiftDrop.Delivery.Tests/Courier/CourierProofStorageTests.cs (new) | SDK contract tests for encryption, private policy/ACL, bytes, conditional create, config/errors/cancellation |
| tests/SwiftDrop.Delivery.Tests/Courier/ProofTestSupport.cs (new) | Synthetic images and isolated fault-controlled storage fixture |
| tests/SwiftDrop.Delivery.Tests/FoundationTests.cs | Proof authentication/private-cache probe and offline cleanup configuration |
| tests/SwiftDrop.Delivery.Tests/Quotes/QuoteApiFactory.cs | Disable live bucket access in every isolated API fixture |
| scripts/verify-foundation.ps1 | Real Gateway proof POST denial; exclude storage secrets/cleanup from offline smoke |
| README.md, docs/courier-phase-4-assessment.md (new), docs/courier-proof-storage.md (new) | Behavior, evidence, exact configuration, rollout, limits and dependencies |

## Builds, tests and actual results

SDK 10.0.301, EF tooling 10.0.9, PostgreSQL 18; the existing disposable random-port
fixture is used. SWIFTDROP_TEST_DELIVERY_DATABASE is cleared in test processes and
SWIFTDROP_TEST_POSTGRES_TEMP_ROOT points to their owned temporary parent. Shared
Neon, live buckets and other services are not mutated. Public NuGet restore uses
the existing process-local configuration; global SDK/source settings are unchanged.

```powershell
dotnet restore SwiftDrop.sln --configfile "$env:TEMP/swiftdrop-courier-nuget.config" --disable-parallel
dotnet build SwiftDrop.sln --no-restore --configuration Release
dotnet test tests/SwiftDrop.Delivery.Tests --no-build --configuration Release --filter 'FullyQualifiedName~CourierProof|FullyQualifiedName~CourierStatus' --logger 'trx;LogFilePrefix=courier-phase4-targeted' --results-directory artifacts/courier-phase4-targeted
dotnet test SwiftDrop.sln --no-build --configuration Release --logger 'trx;LogFilePrefix=courier-phase4-complete' --results-directory artifacts/courier-phase4-complete
dotnet list SwiftDrop.sln package --vulnerable --include-transitive --source https://api.nuget.org/v3/index.json
dotnet ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build
dotnet ef migrations script --idempotent --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --output artifacts/courier-phase4-migrations.sql
dotnet publish src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build --no-restore --output artifacts/courier-phase4-publish
./scripts/verify-foundation.ps1 -Configuration Release
```

- Restore and final Release build passed, zero warnings/errors. Initial builds
  caught SDK v4 renamed ACL/header members and test assertion API/analyzer errors;
  these were corrected before runtime verification. No suppression was added.
- First proof-focused run: 83 passed, zero failed/skipped (1m14s).
- Expanded proof/status selection: 227 passed, zero failed/skipped (2m27s), including
  retained status replay after completion and upgrade/retention checks.
- First full regression: 922 passed, zero failed/skipped; 831 Delivery (5m21s),
  68 Gateway, 12 Identity, 6 Merchant, 5 Notification. Subsequent review added
  malformed/truncated multipart and SDK client/timeout/cancellation tests and
  corrected their safe error handling and full streamed-operation deadlines.
- The next full run found eight invalid-multipart cases returning 500 instead of
  400 (918 passed, 8 failed, 0 skipped overall). Expanding the catch for truncated
  streams had inadvertently omitted InvalidDataException, which is not an
  IOException subtype. The explicit union was restored. No database/storage side
  effects occurred for these rejected requests. Focused corrected security/storage
  run: **44 passed, 0 failed, 0 skipped**; final Release build again zero warnings/errors.
- Final complete regression after that correction: **926 passed, 0 failed,
  0 skipped**: 835 Delivery (5m20s), 68 Gateway, 12 Identity, 6 Merchant and
  5 Notification. This includes all 834 previous-phase cases and 92 added cases
  (91 proof cases plus the foundation authentication probe). Earlier failing
  evidence is retained separately; no failure was skipped or suppressed.
- NuGet audit passed: no known vulnerable packages reported across all 12 projects,
  including transitive dependencies, against the queried public feed. This is not
  a permanent vulnerability guarantee.
- EF check passed with no pending model changes. Idempotent SQL script generated
  successfully, including the proof retention trigger and compatible constraint.
- Publish passed; Linux x64 libSkiaSharp.so is included. This is packaging evidence,
  not proof of native decoding inside a running Linux container.
- Final actual host/Gateway smoke passed: all 12 project references/cycles, all five
  host health/startup checks, 15 missing/internal/readiness probes, original Booking/
  quote/status/read denial checks, new proof POST 401/private/no-store and private
  Delivery listener. All processes started by the script were stopped.
- Docker remains unavailable: docker_engine pipe absent; the sandbox also cannot
  read the user Docker config. A live local MinIO alternative was attempted using
  the official Windows binary URL and archive, both HTTP 410. No binary was run or
  test bucket started. Live object-store/anonymous-denial tests remain unexecuted;
  passing storage tests use a recording SDK client, not a real bucket.
- Final whitespace/protected-source audit passed. Existing Booking transaction/
  input/response, read/auth adapters, Identity, Merchant, Gateway, Worker, Contracts
  and all four earlier migration files are unchanged relative to d663387. No
  staged changes exist. Other repository HEADs and clean working trees remain as
  recorded initially; backend remains on its feature branch at external d663387.

Real PostgreSQL coverage includes atomic proof/status/assignment/history/outbox/
receipt rollback at seven write/commit stages, one winner for concurrent same-
version requests, exact same-key replay, competing Failed state, preflight denial
without uploads, storage missing/corrupt/expired/wrong reference, expiry during
verification, cancellation after staging, second-file failure, unknown commit
acknowledgement and cleanup versus in-flight metadata commit. Original Booking,
read/status, migration and Merchant compatibility tests remain in the full suite.

TRX and outputs are in ignored artifacts/courier-phase4-*; the complete directory
is the final run. No CI run, coverage percentage, load/latency target, live JWT,
broker publication or cloud deployment success is inferred from these results.

## Documentation mismatches and remaining dependencies

The current REST/state/security/data/ER sources agree on upload route, required
proof, types/size, staging, storage ownership and atomic completion. All Proposed
ADRs retain their status. The original PDF's note-or-proof alternative remains
distinct from the user's explicitly approved final photo-OR-signature policy.

REST specifies Parcel projection groups rather than a distinct command receipt;
the implementation reuses the common six-field Booking receipt and leaves private
details on their authorized GET. Docs do not explicitly spell out replay after
Delivered assignment closure; the same-actor minimal stored-result rule preserves
their exact replay guarantee without granting historical detail/download rights.
They also do not specify decoder allocation/frame budgets; the server's technical
limits are recorded above and in the runbook so clients can handle validation.

Deployment documentation still calls Delivery adapters/migrations future and
defers MinIO. No docs/infra repository changes or unapproved shared secret mappings
are made here. The explicit ProofStorage configuration is an application handoff,
not a claim that its credentials/KMS/bucket have been provisioned. The approved
MinIO design now also faces unavailable official Windows binary downloads in this
environment; no alternative provider, public bucket or weakened encryption is
silently substituted.

External acceptance dependencies:

- Identity's real issuer/JWKS/signature/audience/expiry verification and Courier
  role provisioning; production remains fail-closed until integrated.
- Dispatcher/current-assignment and Courier profile provisioning honoring the
  existing parcel/version lock order. No allocation algorithm is introduced.
- A real private MinIO/S3 bucket, dedicated least-privilege Delivery identity,
  TLS, AES256/KMS, bucket ACL/policy and anonymous-access tests. Cloud IAM role
  isolation, versioned cleanup and credential rotation need deployment evidence.
- Reviewed Delivery-only migration rollout and Neon role/TLS/grant verification;
  no shared migration was applied during this task.
- Docker-enabled build/start and native decoder tests in the target container.
- Existing outbox relay/downstream inbox and notification integration. Rows are
  pending and durable; no publication or consumer effect is claimed.
- DownloadProof/receipts are separately documented Customer/Admin operations;
  their two-image selection/streaming contract is not invented by this Courier
  upload checkpoint. React and Flutter Courier clients remain Phase 7 scope.

Reference checks used the current official
[S3 PutObject API](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TPutObjectRequest.html),
[AWS SDK package](https://www.nuget.org/packages/AWSSDK.S3/4.0.104.7),
[SkiaSharp package](https://www.nuget.org/packages/SkiaSharp/4.154.0),
[Linux native package](https://www.nuget.org/packages/SkiaSharp.NativeAssets.Linux.NoDependencies/4.154.0)
and [MinIO KMS guide](https://github.com/minio/minio/blob/master/docs/kms/IAM.md).
The repo contracts remain the SwiftDrop authority; these links verify adapter
APIs/tooling, not acceptance of new business rules.

Next recommended checkpoint: **Phase 5 — failed delivery reporting**, verifying the
existing Phase 3 failure foundation against that phase's complete acceptance list.
Stop after Phase 4. The agent has not staged, committed, pushed, merged or changed
unrelated work. The external d663387 commit is preserved and disclosed above.
