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
- Delivery event publishing/outbox relay remains upstream work. No Notification
  Worker query reaches Delivery-owned tables.

## Verification

Commands, from the backend root:

```powershell
dotnet build --no-restore
dotnet test tests\SwiftDrop.Notification.Tests\SwiftDrop.Notification.Tests.csproj --no-restore
dotnet test tests\SwiftDrop.Merchant.Tests\SwiftDrop.Merchant.Tests.csproj --no-restore --filter "FullyQualifiedName~WebhookApiIntegrationTests.InternalDispatch"
dotnet test tests\SwiftDrop.Delivery.Tests\SwiftDrop.Delivery.Tests.csproj --no-restore --filter "FullyQualifiedName~FoundationTests"
```

In the latest verification run, build passed with 0 warnings and 0 errors;
Notification tests passed 13/13; Merchant tests passed 167/167; Delivery
foundation tests passed 20/20. `git diff --check` passed. The Notification
project was also run with `--collect:"XPlat Code Coverage"`; the collector did
not produce a report in the workspace, so no coverage percentage is claimed.
The full solution test run was attempted earlier. Identity and Delivery
PostgreSQL integration tests require local PostgreSQL binaries or explicitly
configured test databases; these dependencies were unavailable. A dedicated
Notifications test connection was also not configured. Docker/RabbitMQ
end-to-end verification was unavailable because the Docker CLI could not reach
the local engine pipe.

## Runtime inputs still required

Configure these through the private infrastructure environment, without
committing their values:

- `NOTIFICATIONS_DATABASE_CONNECTION_STRING` for the Worker runtime database.
- `WORKER_BROKER_PASSWORD` for the dedicated `worker_bus` account.
- `development/secrets/delivery.jwt`, with audience `delivery-internal`, scope
  `notifications:resolve`, subject `notification-worker`, and a short lifetime.
- `development/secrets/merchant.jwt`, with audience `merchant-internal`, scope
  `webhooks:dispatch`, subject `notification-worker`, and a short lifetime.

The Worker does not mint service credentials. The Identity/team workload-token
provisioning mechanism must supply these files. Run the Notifications migration
with the separate migration credential before starting the Worker. See the
infrastructure README for Compose commands and variable mapping.

## Acceptance status

**Partially complete.** Code, focused Worker/provider tests, internal contract
tests, and the solution build are verified. PostgreSQL concurrency/constraint
verification and live RabbitMQ restart/reconnect/acknowledgment verification
remain blocked on local PostgreSQL/Docker availability. End-to-end Delivery and
Merchant calls also require the private short-lived service token files.

The official event-catalog acceptance tests TC-N15 and TC-REC-03/04 remain
drafted and require an actual event-to-provider run. NFR-02 is a two-second
tracking-update target for subscribed clients and is not established by these
notification-provider tests. NFR-11's producer outbox behavior belongs to the
Delivery publisher; the Worker side commits inbox/jobs before acknowledgment.
NFR-15's RabbitMQ consumer configuration is implemented, but live broker
operation has not been verified in this environment.
