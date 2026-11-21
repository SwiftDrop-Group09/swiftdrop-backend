# Backend container configuration

The shared team workflow is maintained in the sibling infra repository's
`development/compose.yaml` and `development/run.sh`. Compose builds each
backend host as a separate image/container and already supplies the Kestrel
settings described below.

`compose.backend.yaml` is retained as an optional override for other Compose
deployments using these canonical service names. Merge it after the Compose
file that defines the services. It is not needed with `./development/run.sh`.

## Optional Compose override

The override supplies explicit Kestrel endpoint environment variables for all
six backend hosts:

| Hosts | Environment key | Container value |
|---|---|---|
| Gateway, Identity, Delivery, Merchant, Notification Worker, Delivery Outbox Relay | Kestrel__Endpoints__Http__Url | http://0.0.0.0:8080 |
| Delivery only | Kestrel__Endpoints__Grpc__Url | http://0.0.0.0:8081 |

It selects Production configuration so Gateway uses container service DNS
rather than Development loopback destinations.

Explicit Kestrel endpoints take precedence over port-only host settings.
`ASPNETCORE_HTTP_PORTS=8080` and `Grpc__Port=8081` from the reference are
insufficient to replace the scaffold's explicit endpoint URLs. Listener
protocols remain HTTP/1 on 8080 and Delivery HTTP/2 on 8081. No gRPC handler
is implemented.

The shared local workflow validates Compose from `swiftdrop-infra`:

```sh
./development/run.sh config
```

The override includes the `delivery-outbox-relay` service. Set
`DELIVERY_OUTBOX_RELAY_IMAGE` to
`ghcr.io/swiftdrop-group09/swiftdrop-delivery-outbox-relay:sha-<commit>`
using the same published tag as the other backend images. Run Delivery's
migrations and provision the broker publisher before starting the relay. For
deployments with custom networks, connect the relay to the broker's network
in the base Compose file.

Supply `DELIVERY_OUTBOX_DATABASE_CONNECTION_STRING` and
`DELIVERY_BROKER_PASSWORD` through the deployment's private environment.
The relay defaults to the `delivery_bus` publisher on `rabbitmq` in vhost
`/`. Set `DELIVERY_BROKER_HOST`, `DELIVERY_BROKER_USERNAME` or
`DELIVERY_BROKER_VIRTUAL_HOST` when the deployment uses different values.
Use the [outbox database grants](../docs/delivery-outbox-relay-handoff.md#current-relay-behavior)
and scope broker configure and write permissions to the
`SwiftDrop.Contracts.V1:StatusChanged` exchange.

## Gateway request handling

`/health` reports Gateway process liveness. It does not probe downstream services.
The Gateway applies these per-client limits in each replica, configurable with
the corresponding `Gateway__RateLimits__<setting>` environment variable.

| Setting | Default |
|---|---:|
| `AuthenticationPerMinute` | 60 |
| `PublicTrackingPerMinute` | 120 |
| `AssistantPerMinute` (shared by chat and MCP) | 30 |
| `TrackingStreamsPerClient` | 8 open tracking event streams |

When deployed behind a proxy that overwrites forwarding headers, set
`Gateway__Forwarding__KnownProxies__0` to its immediate peer IP address.
Add numbered entries for additional proxies. The Gateway trusts one hop for
client IP and scheme. Configure the ingress to preserve the original HTTP host.
Without a configured trusted proxy, rate limits use the directly connected
peer address.

The Gateway forwards a single UUID in `X-Correlation-ID` and echoes it in its
response. It accepts request bodies up to 12 MiB, including proof uploads with
two 5 MiB parts and multipart overhead. Set any upstream ingress body limit to
allow valid proof uploads. Backend services retain credential, ownership and
payload validation.

## Shared configuration contract

Keys below apply within each owning host, not as global workload-prefixed keys.
The Identity/Delivery/Merchant/Notification groups in
[local.example.json](../config/local.example.json) are reference groupings only.

| Owner/task | Neon database | Private secret name | Owning process key |
|---|---|---|---|
| Identity runtime | swiftdrop_identity | IDENTITY_DATABASE_CONNECTION_STRING | ConnectionStrings__Database |
| Delivery runtime/migration | swiftdrop_delivery | DELIVERY_DATABASE_CONNECTION_STRING | ConnectionStrings__Database |
| Delivery Outbox Relay | swiftdrop_delivery | DELIVERY_OUTBOX_DATABASE_CONNECTION_STRING | ConnectionStrings__DeliveryDatabase |
| Merchant runtime | swiftdrop_merchant | MERCHANT_DATABASE_CONNECTION_STRING | ConnectionStrings__Database |
| Notification Worker | swiftdrop_notifications | NOTIFICATIONS_DATABASE_CONNECTION_STRING | ConnectionStrings__Database |
| Explicit Identity migration | swiftdrop_identity | IDENTITY_MIGRATION_CONNECTION_STRING | ConnectionStrings__MigrationDatabase |
| RabbitMQ container | none | RABBITMQ_DEFAULT_PASS | RABBITMQ_DEFAULT_PASS |

Neon is the selected database provider. Supply actual provisioned TLS connection
strings through private environment inputs or the ignored infra
`development/.env`, never committed JSON, source or build arguments. Each host
receives only its own mapped database setting. Use provisioned least-privilege
roles rather than assuming sample login names exist. Identity's migration login
is separate from runtime. Delivery's explicit migration task currently uses its
listed connection secret, whose login must have Delivery schema DDL rights.
No additional Delivery migration secret is required by this setup.

Normal `up` does not migrate either database. Run the owning explicit task:

```sh
./development/run.sh migrate --mode host identity-service
./development/run.sh migrate --mode host delivery-service
# Or use --mode docker for the same selected migration task.
```

These commands run from the sibling infra repository. They perform DDL only when
explicitly invoked. The connection must target the owning service database and
support the migration's required privileges and transactions. No live Neon
provisioning, role grant or connectivity is claimed by a local build/test result.
The EF design-time factory and Delivery runtime also recognize
DELIVERY_DATABASE_CONNECTION_STRING directly; an explicit
ConnectionStrings:Database overrides that alias.

| Workload | Environment key | Value / private input |
|---|---|---|
| Delivery, Worker | Broker__Host | rabbitmq |
| Merchant | Delivery__GrpcAddress | http://delivery-service:8081 in containers; http://127.0.0.3:8081 for local processes |
| Delivery | Operations__TimeZone | Asia/Colombo |

AMQP uses port 5672 and is not published to the host in the development stack.
Database configuration is consumed by Identity/Delivery persistence. Broker
application adapters remain unimplemented. RABBITMQ_DEFAULT_PASS configures
RabbitMQ's own default administrative login only; it is not passed to Delivery
or Worker. Separate restricted application broker credentials/topology must be
agreed with the messaging owner when that integration starts.

Use the `Broker` section and `Delivery:GrpcAddress` consistently when adding
adapters. The earlier `RabbitMq` and `DeliveryGrpc:Address` names are superseded.
The JSON example leaves credentials empty and is not loaded automatically.
