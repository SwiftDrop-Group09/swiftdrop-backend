# Delivery outbox publishing handoff

## Verified boundary

Phase 4 verifies booking's durable pending outbox and idempotency behavior. It
does not install a publisher, relay, broker transport, consumer or second
messaging stack. A search of the actual backend found no existing relay or
MassTransit/RabbitMQ package/registration. The current responsibility register
lists DEL and MSG as **Unassigned / Pending**; no individual allocation is
invented. Original master Task 4.3 permits this handoff when the relay is absent.

Sources inspected: docs checkout `d97fff4`,
`integration/event-catalog.md`, `integration/integration-design.md`,
`architecture/data-architecture.md` and
`project-management/team-responsibilities.md`. Their design/topology remains
Proposed. Implementation evidence here establishes database persistence and
recovery, not broker delivery or consumer effects.

## Producer records ready for integration

Delivery alone owns `swiftdrop_delivery.outbox_messages`. Booking commits its
parcel, immutable accepted price snapshot, initial history, quote consumption,
completed replay response and one outbox row in the same PostgreSQL transaction.
Only committed rows are candidates for publication. Booking never calls RabbitMQ.

| Field | Existing meaning |
|---|---|
| id | Persisted logical event UUID; reused across retransmission |
| event_type | StatusChanged |
| payload | Complete StatusChanged v1 JSONB body |
| created_at | Same UTC microsecond timestamp as the Created parcel/history |
| published_at | Null while pending; future relay records broker acknowledgement |

The existing pending index is `(created_at, id) WHERE published_at IS NULL`.
Database guards retain immutable logical payload/event identity and one event
per parcel/version/type. They permit the publishing acknowledgement field to
be set. No deletion/competing relay/cleanup policy is added by this phase.

The body contract is the checked-in
`src/BuildingBlocks/SwiftDrop.Contracts/Events/V1/status-changed-v1.schema.json`.
It contains exactly eventId, eventType, schemaVersion, occurredAt, parcelId,
parcelVersion, merchantId, status, previousStatus and correlationId. Customer
creation uses Created/version 1, null previousStatus and null merchantId. The
event UUID, parcel version, creation timestamp and correlation are fixed when
booking commits. Retries must retain that same body and event ID.

Do not serialize addresses, recipient/customer identity or contact, tracking
codes, private notes, credentials, GPS, proof data or raw storage locations.
The body correlationId is an opaque bounded string; preserve it without
assuming it is a GUID. Do not log the entire payload.

## Publishing and consumer acceptance still required

The DEL/MSG integration owners must provide the transport DTO/serializer,
publisher and relay with compatible pinned runtime packages. The current
proposed catalog specifies:

- Explicit message entity `SwiftDrop.Contracts.V1:StatusChanged`, durable fanout.
- `notification-status` and `delivery-realtime` durable receive endpoints/bindings.
- Least-privilege workload credentials and topology provisioning before demo
  writes. The current infra checkout does not establish dedicated workload users.
- Broker acknowledgement before published_at is recorded. A failure must leave
  the event pending; it must never delete/compensate a committed parcel.
- A crash after acknowledgement but before marking can republish. Reuse eventId;
  consumers must deduplicate their effects durably before ACK.

The catalog's consumer retry/error-queue rules are not a defined relay polling
schedule. Confirm relay claiming/coordination, backoff, monitoring and safe
shutdown with the allocated owners rather than inventing another topology.
No future relay may mutate quote input, accepted prices, exact replay envelopes
or idempotency tombstones. A client retry always uses the same principal,
operation, key and canonical confirmation.

Broker-enabled integration must still demonstrate actual publish confirmation,
stable identity through retransmission, outage/restart recovery, scoped topology
permissions, crash recovery after confirmation, consumer effect deduplication
and monitored pending/error queues. These outcomes are not claimed by a passing
booking test or the existence of a database row.

## Evidence supplied by Phase 4

The reliability suite uses real isolated PostgreSQL. It proves one logical
booking/event under 20 identical requests, different-key quote races, key-body
conflicts, lost response recovery, simulated lost COMMIT acknowledgement,
observed lock waits/timeouts/cancellation and database failures through COMMIT.
It validates persisted events against every constraint used by the checked-in
flat v1 schema and verifies privacy and stable event/correlation retention.

The no-broker-adapter test runs booking with PostgreSQL only and leaves a
schema-valid event pending. It proves that absent broker integration cannot
gate or roll back this booking boundary. It is not a RabbitMQ transport fault
test, publish confirmation test or notification delivery result.
