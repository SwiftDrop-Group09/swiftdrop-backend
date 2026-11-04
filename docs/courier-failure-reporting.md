# Failed delivery reporting handoff

29 October 2026. FR-R04 / Phase 5 / TC-R04 use the existing
**PUT /api/v1/deliveries/{id}/status**, operation UpdateDeliveryStatus.
There is no separate failure endpoint or specialized failure event.

Require a verified human Courier, current Active assignment and Idempotency-Key.
The parcel must be Assigned, PickedUp, InTransit or OutForDelivery. Use its latest
authorized version as expectedVersion. Example body (version is illustrative):

```json
{
  "status": "Failed",
  "expectedVersion": 5,
  "failureReason": "RecipientUnavailable",
  "notes": "Optional private delivery note"
}
```

failureReason must be exactly RecipientUnavailable, AddressUnreachable, Damaged
or Other. Free text, numeric values/strings and different spelling are rejected.
Notes are optional; Other is valid without notes. The existing shared StatusInput
note limit is 500 characters; text is trimmed and blank notes normalize to absent.
No photo, signature, object key, recipient name, caller Courier ID or proof flag
is required or accepted for failure reporting. Identity comes from the verified
principal, not a request field. Status/reason/note cannot bypass strict JSON fields.

An accepted request returns 200 with the original six-field Parcel receipt
(id, trackingNumber, status, version, priceSnapshot, createdAt). No failure reason,
note, actor ID, address, proof or storage reference appears in that receipt.
All status responses are private/no-store.

The transaction increments Version once, sets parcel Failed, closes the retained
assignment as Failed, appends private history with previous state/reason/note/
actor/UTC timestamp, appends one redacted StatusChanged v1 and completes the replay
receipt. Tracking, owner, original quote and immutable price snapshot are unchanged.
The assignment's pickup timestamp is retained when present; DeliveredAt stays null.
Its Active slot is released, it leaves the active manifest and no replacement
assignment is created. A broker outage does not undo this commit; the pending
outbox is not evidence of publication or notification delivery.

## Errors and safe retry

| Result | Meaning / action |
|---|---|
| 400 validation | Required reason missing/unknown, invalid version/note/body/key; correct input; no reservation or mutation was committed |
| 401 unauthorized | No verified identity; authenticate through Identity |
| 403 forbidden | Caller lacks Courier role; Dispatcher/Admin cannot impersonate the Courier |
| 404 not_found | Parcel missing or caller lacks the required assignment relationship; no currentVersion is disclosed |
| 409 stale_version | Authorized version mismatch; reload before choosing an intentional new action |
| 409 invalid_transition | The requested new action is not permitted, including a terminal Failed retry/resume |
| 409 idempotency_conflict | Same principal/operation/key has a different parcel/status/version/reason/note fingerprint |
| 409 key_expired | The retained key is at least 30 days old; it is not silently reused |
| 409 operation_in_progress | Lock contention; Retry-After: 1; retry original request/key |
| 503 delivery_unavailable | Dependency/write failure or uncertain commit; retry original request/key to resolve the outcome |

For a timeout, lost response or unknown commit acknowledgement, retry with the
same key and original body, including original expectedVersion. Exact committed
replay precedes current state/version checks and emits no new event. A changed
failure reason or private note is a different request, even when the status is the
same. The original Courier may retrieve their own minimal receipt through the
retained Failed assignment; a former Reassigned Courier gets no such permission.
This does not reopen the assignment or grant private details/download access.

Do not rotate the key automatically after a timeout. Do not report failure as a
compensation for an unknown successful delivery: reconcile the original proof key
first. Failed and Delivered are both terminal. A different key cannot overwrite
the winning failure reason, change Failed back to Created/Assigned/another stage,
or complete it with proof. Two competing failures from the same expected version
can commit only one reason/history/event; identical same-key requests replay that
one result. A failed DB transaction restores the Active assignment, version and
all history/outbox/replay rows, and the original key can be retried after recovery.

## Privacy, persistence and boundaries

Only the owning Customer or permitted Operations users retain private parcel/history
access after failure, under the existing GET authorization policy. Another Customer
gets 404; the closed Courier's minimal replay permission does not confer detail
access. Failure reasons and notes are absent from StatusChanged, public projections
and replay receipts. Merchant-originated failures retain merchant provenance in the
same canonical event; no DeliveryFailed event or direct webhook/notification is sent.

The existing parcel-first/assignment row locks, fresh ownership recheck and EF
Version/xmin predicates protect failure against concurrent mutation/reassignment.
All dispatcher writers must honor the shared version/lock contract. No auto
allocation, capacity counter, return workflow or Failed-to-Created transition is
invented here. The documented future new-booking/reference/manual return process
does not add a new reference field to the existing Customer booking input.

No new migration, database column, package, parcel state, assignment rule, route or
event schema is needed for this checkpoint. Current production authentication
remains fail-closed until real Identity verification is integrated. Positive tests
use the existing isolated trusted-principal fixture and disposable PostgreSQL.
The failure path needs neither proof storage nor a reachable broker to commit.
Live JWT/provisioning, dispatcher integration, Neon deployment and eventual
outbox delivery remain separate integration dependencies. Courier history and
React/Flutter failure screens remain their later requested phases.

See the [Phase 5 assessment and actual evidence](courier-phase-5-assessment.md).
