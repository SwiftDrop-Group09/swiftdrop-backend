# Backend container configuration

The backend follows the proposed configuration contract in the
[documentation reference Compose at 233f707](https://github.com/SwiftDrop-Group09/swiftdrop-docs/blob/233f7077f54949c0cacc31f11b203bf4a27d02b4/deployment/compose.yaml).
Infrastructure deployment files must use the same keys and per-workload identities.
No database or RabbitMQ adapters are implemented by this configuration change.

## Required Compose override

Always merge `compose.backend.yaml` **after** the reference Compose. It supplies
explicit Kestrel endpoint environment variables for all five backend hosts:

| Hosts | Environment key | Container value |
|---|---|---|
| Gateway, Identity, Delivery, Merchant, Worker | Kestrel__Endpoints__Http__Url | http://0.0.0.0:8080 |
| Delivery only | Kestrel__Endpoints__Grpc__Url | http://0.0.0.0:8081 |

The override selects Production configuration for both ASP.NET Core and .NET
hosting. This keeps Development's loopback YARP destinations out of containers;
the base configuration supplies private service DNS destinations. Local
`dotnet run --launch-profile http` continues to use the unchanged Development
loopback listeners.

Explicit Kestrel endpoints take precedence over port-only host settings.
`ASPNETCORE_HTTP_PORTS=8080` and `Grpc__Port=8081` from the reference are
insufficient to replace the scaffold's explicit endpoint URLs. Listener
protocols remain HTTP/1 on 8080 and Delivery HTTP/2 on 8081. No gRPC handler
is implemented.

From the backend repository root, validate the merged model without substituting
credentials or requiring a running Docker daemon:

```sh
docker compose -f ../swiftdrop-docs/deployment/compose.yaml -f deployment/compose.backend.yaml --profile application config --no-interpolate --quiet
```

For validation with locally configured values, prepare the reference's ignored
environment file as described in its deployment guide, then run:

```sh
docker compose --env-file ../swiftdrop-docs/deployment/.env -f ../swiftdrop-docs/deployment/compose.yaml -f deployment/compose.backend.yaml --profile application config --quiet
```

Use both `-f` arguments in that order for future deployment commands too. The
reference is still a design baseline: application images, healthcheck executable,
dependency-aware readiness and deployment runtime verification remain future
milestones. This override adds no image/build definition, network, service,
published port or credentials. The reference alone does not apply this fix.

Only Gateway's HTTP container port is published at `127.0.0.1:5000`.
API/Worker HTTP and Delivery gRPC stay on the private container network.
When infrastructure owns a full Compose implementation, carry these environment
overrides into its five existing backend service definitions.

## Shared configuration contract

Keys below apply within each owning host, not as global workload-prefixed keys.
The Identity/Delivery/Merchant/Notification groups in
[local.example.json](../config/local.example.json) are reference groupings only.

| Workload | Neon database | Neon role | Sample environment variable |
|---|---|---|---|
| Identity | swiftdrop_identity | identity_app | IDENTITY_DATABASE_CONNECTION_STRING |
| Delivery | swiftdrop_delivery | delivery_app | DELIVERY_DATABASE_CONNECTION_STRING |
| Merchant | swiftdrop_merchant | merchant_app | MERCHANT_DATABASE_CONNECTION_STRING |
| Notification Worker | swiftdrop_notifications | notifications_app | NOTIFICATIONS_DATABASE_CONNECTION_STRING |

Each host receives only its own `ConnectionStrings__Database`. The sample
connection strings use a placeholder Neon endpoint and require TLS; replace them
with the actual Neon connection details after databases and roles are created.
Do not pass all four service credentials to every host.

| Workload | Environment key | Value / private input |
|---|---|---|
| Delivery, Worker | Broker__Host | rabbitmq |
| Delivery | Broker__Username | delivery_bus |
| Delivery | Broker__Password | DELIVERY_BROKER_PASSWORD |
| Worker | Broker__Username | worker_bus |
| Worker | Broker__Password | WORKER_BROKER_PASSWORD |
| Merchant | Delivery__GrpcAddress | http://delivery-service:8081 in containers; http://127.0.0.3:8081 for local processes |
| Delivery | Operations__TimeZone | Asia/Colombo |

AMQP uses port 5672; the reference does not publish it to the host. The private
`rabbitmq` DNS name therefore requires that network and is not a working broker
address for a bare host process. The local example reserves future adapter
configuration without claiming live connectivity.

Use the `Broker` section and `Delivery:GrpcAddress` consistently when adding
adapters. The earlier `RabbitMq` and `DeliveryGrpc:Address` names are superseded;
no runtime compatibility alias or credential fallback is installed. Keep each
database and broker password private and distinct. The example deliberately
leaves passwords empty and is not loaded automatically.
