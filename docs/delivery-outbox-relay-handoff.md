# Delivery outbox relay implementation and handoff

## Verified boundary

Earlier Phase 4 verified booking's durable pending outbox and idempotency
behavior without installing a publisher. On 17 November 2026, a separate
`SwiftDrop.Delivery.OutboxRelay` worker was added to publish those rows. The
Delivery API still has no RabbitMQ or MassTransit dependency. This source change
does not establish live-broker acceptance, production credential provisioning,
or an organizational ownership assignment.

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

## Current relay behavior

The relay uses the existing pinned MassTransit RabbitMQ stack and contract DTO.
It reads pending rows in creation order with `FOR UPDATE SKIP LOCKED`, publishes
to the configured v1 fanout exchange, then updates `published_at` in the same
database transaction after publisher confirmation. If publish or database
commit fails, the row remains pending. A crash after broker confirmation but
before the database commit can republish the event; Notification's durable inbox
deduplicates by event ID before acknowledging.

The relay uses a separate `swiftdrop_delivery` connection. Its runtime database
role should receive only:

```sql
GRANT CONNECT ON DATABASE swiftdrop_delivery TO swiftdrop_outbox_relay;
GRANT USAGE ON SCHEMA public TO swiftdrop_outbox_relay;
GRANT SELECT ON TABLE public.outbox_messages TO swiftdrop_outbox_relay;
GRANT UPDATE (published_at) ON TABLE public.outbox_messages TO swiftdrop_outbox_relay;
```

Do not use a schema-owner or migration login for the relay.

## Local-development wiring

The infrastructure Compose stack adds a `delivery-outbox-relay` process and
provisions separate `delivery_bus` and `worker_bus` RabbitMQ users through the
management API bootstrap script. The runner generates their passwords into the
ignored development environment file. The relay can run only when its dedicated
Delivery connection and broker credentials are configured.

Identity also has a Development-only
`--run-development-workload-token-provisioner` CLI mode. It issues four-minute
JWTs for exactly the Notification Worker's Delivery and Merchant scopes,
refreshes them every two minutes, writes them atomically with private file
permissions, and never returns them over HTTP. Compose runs that process
with the private signing-key volume read-only and gives the Worker only a
read-only mount of the separate token volume. Host mode writes to a private
temporary directory; an external RabbitMQ with matching credentials is still
required.

The local Notification database migration is available with
`development/run.sh migrate notification-worker` or the equivalent Windows
runner command. The full stack applies it only when a Notification database
connection is configured.

## Runtime acceptance still required

## Verification run on 17 November 2026

- `dotnet build --no-restore` passed for the full solution with zero warnings
  and zero errors.
- New outbox payload parser tests passed 5/5. They cover the canonical v1
  payload, opaque correlation preservation, event-type and event-ID mismatch,
  invalid schema version, and malformed JSON.
- Development token provisioner tests passed 2/2. They check the Development
  environment guard and the emitted audiences, exact scopes, subject, unique
  token IDs, and four-minute lifetime.
- Notification Worker tests passed 16/16, covering duplicate-event inbox
  deduplication, durable provider jobs, and Worker-to-Merchant routing. Merchant
  internal-dispatch tests passed 2/2. Delivery foundation tests passed 20/20.
- Worker-to-Merchant routing tests assert the outgoing bearer token and internal
  payload, leave the durable job pending
  when the token file is absent, recover when it appears, and retry a Merchant
  503 after backoff before marking the job accepted. The Merchant endpoint's
  separate integration tests passed 2/2.
- The full solution test command exited nonzero: Identity passed 110 and failed
  259; Delivery passed 604 and failed 392. The reported persistence failures
  require local PostgreSQL binaries or an explicitly configured isolated test
  database, neither of which is available here. Gateway passed 68 and Merchant
  passed 167 in that run.
- Docker is installed but its engine is unavailable, so no live RabbitMQ
  publish/acknowledgement, outage/restart, or broker-to-Worker end-to-end run was
  possible. These unit and service tests do not establish the relay's database
  transaction behavior or live consumer effects.

The current proposed event catalog specifies:

- Explicit message entity `SwiftDrop.Contracts.V1:StatusChanged`, durable fanout.
- `notification-status` and `delivery-realtime` durable receive endpoints/bindings.
- Least-privilege workload credentials and topology provisioning before demo
  writes. Local Docker topology is provisioned automatically; production
  workload-token and broker-user provisioning remains an environment task.
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
and monitored pending/error queues. These outcomes are not claimed by the
source implementation or the existence of a database row.

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
