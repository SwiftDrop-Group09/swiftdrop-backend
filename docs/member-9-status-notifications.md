# Member 9: Status Events and Notifications

## Implemented behavior

- `StatusNotificationConsumer` consumes `StatusChangedV1` from the durable
  `notification-status` queue bound to the v1 fanout exchange. RabbitMQ prefetch
  is 16 and endpoint concurrency is 8. MassTransit acknowledges only after
  `StatusNotificationProcessor` returns; invalid or repeatedly failing messages
  use the broker's error queue handling.
- `StatusNotificationProcessor` validates the v1 payload and commits the inbox
  record and derived jobs in one Notifications database transaction. Event ID
  uniqueness is enforced by the inbox index. PostgreSQL advisory transaction
  locks serialize versions for the same parcel. Older or repeated parcel
  versions are recorded in the inbox without generating downstream jobs.
- Customer status events create durable InApp and MockSms jobs. Merchant events
  create a durable webhook dispatch request. InApp jobs reach `Persisted`;
  MockSms attempt outcomes are recorded durably and retry after 5 seconds, 30
  seconds, and 2 minutes, then become `Failed` after the fourth attempt.
- The routing Worker calls Delivery's authenticated
  `/internal/v1/notification-context` operation and Merchant's authenticated
  `/internal/v1/webhook-dispatches` interface. It stores work before making
  these calls, uses leases to recover abandoned claims, and schedules retries
  for transient failures. Merchant retains ownership of webhook delivery,
  signing, attempts, and replay.
- Routing tests verify the Worker's Merchant HTTP request path, bearer-token
  forwarding, `StatusChanged` payload fields, durable Accepted state, missing
  token recovery after token-file rotation, and 503 retry followed by success.
  Merchant's internal-dispatch integration tests separately verify its real
  API accepts the authorized request and creates durable webhook jobs. These
  tests exercise the two service boundaries independently; they do not replace
  live RabbitMQ event-to-webhook acceptance.
- A separate `SwiftDrop.Delivery.OutboxRelay` process claims committed Delivery
  outbox rows with `FOR UPDATE SKIP LOCKED`, publishes the existing
  `StatusChangedV1` contract, and marks rows published only after MassTransit
  publish confirmation. It uses a separate connection and does not make the
  Delivery API depend on RabbitMQ. Duplicate delivery after a confirmed publish
  and before the database commit is expected and handled by the Worker inbox.

## Verification

Commands, from the backend root:

```powershell
dotnet build --no-restore
dotnet test tests\SwiftDrop.Notification.Tests\SwiftDrop.Notification.Tests.csproj --no-restore
dotnet test tests\SwiftDrop.Merchant.Tests\SwiftDrop.Merchant.Tests.csproj --no-restore --filter "FullyQualifiedName~WebhookApiIntegrationTests.InternalDispatch"
dotnet test tests\SwiftDrop.Delivery.Tests\SwiftDrop.Delivery.Tests.csproj --no-restore --filter "FullyQualifiedName~FoundationTests"
```

In the latest verification run, build passed with 0 warnings and 0 errors;
Notification tests passed 13/13 (including invalid payload rejection and
failure-then-success recovery); Merchant tests passed 167/167; Delivery
foundation tests passed 20/20. `git diff --check` passed. The Notification
project was also run with `--collect:"XPlat Code Coverage"`; the collector did
not produce a coverage report, so no coverage percentage is claimed.
The full solution test run was attempted earlier. Identity and Delivery
PostgreSQL integration tests require local PostgreSQL binaries or explicitly
configured test databases; these dependencies were unavailable. A dedicated
Notifications test connection was also not configured. Docker/RabbitMQ
end-to-end verification was unavailable because the Docker CLI could not reach
the local engine pipe.

On 17 November 2026, the current checkout was rebuilt and the focused verification
was rerun: solution build passed with 0 warnings/errors; relay parser tests passed
5/5; token provisioner tests passed 2/2; Notification tests passed 16/16;
Merchant internal dispatch passed 2/2;
Delivery foundation tests passed 20/20. A full solution run also completed with
Gateway 68/68 and Merchant 167/167 passing, Identity 110 passed / 259 failed,
and Delivery 604 passed / 392 failed. The observed Identity and Delivery
failures require PostgreSQL binaries or an explicitly configured isolated test
database. Docker Desktop's engine was unavailable, so broker-backed relay and
end-to-end Notification acceptance remains unverified.

## Runtime inputs still required

Configure these through the private infrastructure environment, without
committing their values:

- `NOTIFICATIONS_DATABASE_CONNECTION_STRING` for the Worker runtime database.
- `WORKER_BROKER_PASSWORD` for the dedicated `worker_bus` account.
- `DELIVERY_OUTBOX_DATABASE_CONNECTION_STRING` for the relay's dedicated
  `swiftdrop_delivery` role. Grant only `SELECT` on
  `public.outbox_messages` and `UPDATE(published_at)`; the latter also allows
  row-level claims. Do not reuse a DDL/migration connection.
- `DELIVERY_BROKER_PASSWORD` for the relay's dedicated `delivery_bus` account.
- For Docker development, `identity-workload-token-provisioner` renews the two
  short-lived files in a private named volume. It runs the Identity
  `--run-development-workload-token-provisioner` CLI mode, available only in
  Development. The Worker mounts that token volume read-only. The CLI does not
  expose a token-minting HTTP endpoint.

The local Compose setup initializes `delivery_bus` and `worker_bus` with
separate generated passwords and exchange/queue permissions. The infrastructure
runner supports the explicit `notification-worker` migration command and applies
that migration during startup only when a Notification database connection is
configured. Host mode still requires a reachable RabbitMQ instance and matching
user credentials to enable broker consumption.

## Acceptance status

**Implementation present; runtime acceptance pending.** Previous focused
Worker/provider tests, internal contract tests, and solution build are recorded
above as historical evidence. PostgreSQL concurrency/constraint
verification and live RabbitMQ restart/reconnect/acknowledgment verification
were not performed for the new relay/token-provisioning implementation during
this change. End-to-end Delivery and Merchant calls require configured databases
and the local or externally provisioned short-lived service tokens.

The official event-catalog acceptance tests TC-N15 and TC-REC-03/04 remain
drafted and require an actual event-to-provider run. NFR-02 is a two-second
tracking-update target for subscribed clients and is not established by these
notification-provider tests. NFR-11's producer outbox behavior belongs to the
Delivery publisher; the Worker side commits inbox/jobs before acknowledgment.
NFR-15's RabbitMQ consumer and a separate outbox publisher are implemented in
source, but live broker operation has not been verified in this environment.
