# Courier Phase 4: private proof and completion

Scope: Phase 4 only, FR-R03 and FR-R02's final completion guard. No dispatcher,
failure-client, history, receipt, React or Flutter work. No commit/push/merge.

## Inspection before implementation

Backend remains feat/courier-workflow-persistence at 4723578. The 31 existing
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

Implementation and verification in progress.
