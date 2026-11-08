# Private Courier proof storage

Phase 4 implements POST /api/v1/deliveries/{id}/pod, operation
CompleteDeliveryWithProof. Human Courier only, current assignment and
OutForDelivery required. Supply Idempotency-Key and multipart fields:

| Field | Contract |
|---|---|
| photo | Optional JPEG/PNG file, <=5 MiB |
| signature | Optional JPEG/PNG file, <=5 MiB |
| recipientName | Required nonblank text, <=120 characters |
| expectedVersion | Required positive integer from the latest authorized parcel |

At least one image is required; both are accepted, combined input <=10 MiB.
There is no caller-supplied actor, object key, proof ID or completed-upload flag.
Unknown/duplicate fields, unexpected query parameters, nonmultipart bodies,
oversized/empty images and unsupported/spoofed MIME types fail with 400.
Image pixels must fully decode; a filename or magic prefix alone is insufficient.
Images are re-encoded without source metadata/comments; raw EXIF/GPS is not stored.
The original file hashes, type, parcel, recipient and expectedVersion bind replay.
Same-key retries must send the original content and fields, even after Delivered.

Technical safeguards: multipart total input <=10 MiB+64 KiB framing, <=4 KiB section
headers, <=1 KiB per text field, single-frame images <=16,777,216 pixels (64 MiB
RGBA), two concurrent decoders per service instance. These are server resource
budgets, not additional business proof types. Re-encoding may change stored size;
the 5 MiB upload limit applies to original files. Container deployment includes
the pinned SkiaSharp Linux native assets; actual image startup must be verified
in the target container. HTTP resource limits also apply at the configured ingress.

## Explicit storage configuration

No bucket is created automatically and no filesystem/public upload fallback exists.
Only Delivery gets this credential. Set these keys in Delivery user secrets or
its process environment; do not pass them to other services or commit actual values.

| Configuration | Environment | Meaning |
|---|---|---|
| ProofStorage:Endpoint | ProofStorage__Endpoint | S3 service origin; HTTPS except loopback HTTP for local MinIO |
| ProofStorage:Bucket | ProofStorage__Bucket | Dedicated existing private bucket |
| ProofStorage:Region | ProofStorage__Region | Actual signing region; local MinIO commonly us-east-1 |
| ProofStorage:AccessKey | ProofStorage__AccessKey | Explicit Delivery-only access ID |
| ProofStorage:SecretKey | ProofStorage__SecretKey | Private Delivery-only secret |
| ProofStorage:SessionToken | ProofStorage__SessionToken | Optional explicit session credential token |

No ambient AWS profile/metadata credential discovery is used. Endpoint userinfo,
paths, query secrets, fragments and nonloopback plaintext HTTP are rejected.
HTTP redirects are disabled and requests have a 30-second storage timeout with
no automatic SDK retry; unknown outcomes become cleanup candidates. Never log
credentials or proof/request bytes. Rotation requires replacing the configured
credential and restarting the service singleton.

Use MinIO in local development and S3 in cloud per the ER storage contract. Provision
private access and server-side AES256 encryption (MinIO requires its supported
KMS/encryption setup). The adapter requests AES256 and verifies it on read-back;
unencrypted or inaccessible storage fails closed with 503. It never silently
downgrades encryption to make an unconfigured local bucket work.

The adapter reads the bucket ACL and policy before storage access. ACL grants must
be owner-only; any resource-policy Allow is rejected. Use an IAM/MinIO identity
policy for the dedicated Delivery credential; deny-only bucket transport policies
are supported. Unreadable/unsupported ACL/policy checks fail closed. Block public
access at the account and bucket, keep direct anonymous object requests denied,
and verify those controls against the deployed storage service. No public ACL,
presigned URL, download token or storage reference is sent to the client.

Provision only the required actions on this bucket/prefix: GetBucketAcl,
GetBucketPolicy, ListBucket restricted to proofs/, and PutObject/GetObject/
DeleteObject for proofs/*. If bucket versioning is enabled, metadata/delete
version access is also needed to remove actual orphan versions. Application code
must not create policies/buckets or have broad administration permissions. Do not
configure an unconditional age-based lifecycle on proofs/: committed references
must be retained. The reference-aware application cleanup owns orphan removal.

## Commit, replay and cleanup

Preflight authorizes the assignment, resolves replay and validates version/state
in a short rolled-back transaction before decoding/storage. Every accepted image
gets a generated proofs/{parcel UUID}/{upload UUID}/{photo|signature}.{jpg|png} key.
Conditional If-None-Match:* prevents replacement; storage is read back and hashed.

The final transaction locks parcel then assignment and rechecks ownership, state,
version and idempotency. It takes shared per-object advisory locks, verifies bytes,
encryption, MIME, size and staging age (<24h), then saves ProofMetadata, Delivered
parcel/version, Delivered assignment/timestamp, append-only history, unchanged
StatusChanged v1 and exact 200 replay in one transaction. No broker call occurs.
The assignment releases its Active slot. Status PUT still cannot set Delivered.
Committed proof metadata is immutable and retained through EF and PostgreSQL.

After an unknown commit/HTTP outcome retry the same key and original files. A new
key is a new action and may conflict. An identical stored result is returned before
storage/state/version checks, including during a storage outage. The original
Delivered Courier may replay only their own minimal receipt, and earlier status
receipts; this does not grant historical parcel details or proof downloads. Former
Reassigned Couriers still receive 404. Receipt bodies contain only the existing six
Parcel fields and price snapshot; no recipient name, object key or private proof.

ProofCleanupWorker runs at startup and every 24 hours when a bucket is configured.
It lists generated proofs/ keys, considers objects older than 24 hours, acquires
an exclusive nonblocking advisory lock for each candidate, then checks committed
ProofMetadata in a fresh statement before deletion. Busy or referenced candidates
are retained. Completion holds a matching shared lock through commit, so cleanup
cannot delete a referenced/about-to-commit object. All proof writers must follow
this contract. Database failure never skips reference checks; storage failure
leaves candidates for the next pass. Logs report only a count or safe unavailable
message. A failed pass may already have removed earlier confirmed orphans.

Database rollback, cancellation, second-file failure, concurrent losers and lost
commit acknowledgement can leave objects. There is no eager deletion on ambiguous
outcomes and no claim of a distributed transaction. Crashes are recovered by the
next cleanup pass; committed references remain protected. An object older than
24 hours cannot be newly committed through the completion handler.

## Deployment and boundaries

Review/apply 20261028175800_CourierProofCompletion through the existing Delivery
migration task. It adds the proof-retention trigger and extends only the replay
constraint for CompleteDeliveryWithProof 200, preserving all prior status/Booking
envelopes. No shared Neon migration was applied during implementation. Downgrade
with retained proof 200 receipts safely fails; retain the compatible schema when
rolling back application code or use a reviewed forward migration. Never delete
proof/replay records to force a downgrade.

Production Identity validation still must be integrated; no fake production
authentication is enabled. This phase implements Courier upload/completion only.
DownloadProof/receipt are separately documented Customer/Admin operations; no
download selection contract for two images is invented here. Proof bytes stay
behind private bucket access. Customer streaming, receipt generation, broker
consumers and React/Flutter Courier flows remain integration work.

Live MinIO/S3 permissions, anonymous denial, encryption/KMS, TLS, versioned cleanup,
container-native decoding and shared Neon deployment require their actual
environment. Isolated adapter tests and disposable PostgreSQL evidence are
recorded separately in the [Phase 4 report](courier-phase-4-assessment.md).
