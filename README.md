# SwiftDrop Backend

Foundation for the SwiftDrop university Enterprise Application Development project.
Targets .NET 10. Only process liveness and gateway forwarding are implemented.
Business routes, authentication, persistence, messaging, GraphQL, SSE and gRPC
handlers are not implemented.

Architecture follows the proposed [documentation baseline at 233f707](https://github.com/SwiftDrop-Group09/swiftdrop-docs/tree/233f7077f54949c0cacc31f11b203bf4a27d02b4).
Architecture approval is still pending. See its
[service catalog](https://github.com/SwiftDrop-Group09/swiftdrop-docs/blob/233f7077f54949c0cacc31f11b203bf4a27d02b4/architecture/service-catalog.md)
and [environment matrix](https://github.com/SwiftDrop-Group09/swiftdrop-docs/blob/233f7077f54949c0cacc31f11b203bf4a27d02b4/deployment/environments.md).

## Projects and ownership

`SwiftDrop.sln` is the single root solution.

| Project | Location | Boundary / future private database |
|---|---|---|
| SwiftDrop.Gateway | src/Gateway/SwiftDrop.Gateway | YARP; no database or service assembly references |
| SwiftDrop.Identity.Api | src/Services/Identity/SwiftDrop.Identity.Api | Identity / swiftdrop_identity |
| SwiftDrop.Delivery.Api | src/Services/Delivery/SwiftDrop.Delivery.Api | Delivery operations, GraphQL, SSE, internal gRPC / swiftdrop_delivery |
| SwiftDrop.Merchant.Api | src/Services/Merchant/SwiftDrop.Merchant.Api | Merchant integration and webhook HTTP attempts / swiftdrop_merchant |
| SwiftDrop.Notification.Worker | src/Workers/SwiftDrop.Notification.Worker | Future consumers/provider jobs; private health host only today / swiftdrop_notifications |
| SwiftDrop.Contracts | src/BuildingBlocks/SwiftDrop.Contracts | Dependency-free, versioned integration contract assets |
| SwiftDrop.Identity.Tests | tests/SwiftDrop.Identity.Tests | Identity host foundation |
| SwiftDrop.Delivery.Tests | tests/SwiftDrop.Delivery.Tests | Delivery host foundation |
| SwiftDrop.Merchant.Tests | tests/SwiftDrop.Merchant.Tests | Merchant host foundation |
| SwiftDrop.Notification.Tests | tests/SwiftDrop.Notification.Tests | Worker host foundation |
| SwiftDrop.Gateway.Tests | tests/SwiftDrop.Gateway.Tests | Documented route ownership and internal-path isolation |

Each API has Domain, Application and Infrastructure folders. Namespaces follow
project and folder names, e.g. `SwiftDrop.Delivery.Api.Application`.
Domain has no transport or infrastructure dependency; Application uses Domain
and declares ports; Infrastructure implements those ports. Presentation calls
Application. These boundaries are review conventions inside each API project.

APIs and the Worker reference Contracts only. Tests reference their own host;
Gateway and Contracts reference no other project. No API references another API.
Contracts contain versioned wire schemas/proto assets, no shared entities or
domain rules. No code generation or transport package is enabled yet.

## Build and test

Install SDK **10.0.301**, pinned exactly in `global.json`, and Git. No database,
broker, object store or credentials are needed for this scaffold.

```sh
dotnet --version
dotnet restore SwiftDrop.sln
dotnet build SwiftDrop.sln --no-restore --configuration Release
dotnet test SwiftDrop.sln --no-build --configuration Release
```

The xUnit integration tests use ASP.NET Core TestServer and do not bind TCP
ports. They check liveness, absent feature endpoints, and the real YARP routing
pipeline using a test-only transport. The route fixture is copied from baseline
233f707; update it deliberately when the canonical route inventory changes.

GitHub Actions runs restore, Release build, tests and process smoke checks on
pull requests, main/chore branch pushes, and manual dispatch. A hosted workflow
run is still required to establish CI execution evidence.

## Run locally without containers

Run each command in its own terminal from this repository root:

```sh
dotnet run --project src/Services/Identity/SwiftDrop.Identity.Api --launch-profile http
dotnet run --project src/Services/Delivery/SwiftDrop.Delivery.Api --launch-profile http
dotnet run --project src/Services/Merchant/SwiftDrop.Merchant.Api --launch-profile http
dotnet run --project src/Workers/SwiftDrop.Notification.Worker --launch-profile http
dotnet run --project src/Gateway/SwiftDrop.Gateway --launch-profile http
```

The Development configuration uses separate loopback addresses so all private
HTTP hosts can retain port 8080 on one machine:

| Host | Development liveness |
|---|---|
| Gateway | http://127.0.0.1:5000/health |
| Identity | http://127.0.0.2:8080/health |
| Delivery | http://127.0.0.3:8080/health |
| Merchant | http://127.0.0.4:8080/health |
| Worker | http://127.0.0.5:8080/health |

Delivery also reserves `127.0.0.3:8081` for internal HTTP/2; no gRPC method is
mapped. These loopback-only process-development addresses are diagnostics, not
additional published deployment ports or client entry points. Clients use Gateway.

After the Release build, run `./scripts/verify-foundation.ps1` in PowerShell
(5.1+ on Windows, PowerShell 7 on Linux/macOS). It checks the project reference
graph, starts all five hosts, verifies health and missing endpoints, checks
gateway forwarding and the HTTP/2 listener, then stops only the processes it
started. The listed local ports must be free; stop manually running hosts first.

A successful `GET /health` returns HTTP 200 and `Healthy`, meaning only that the
process can respond. No dependency readiness is claimed; `/ready` returns 404.
Unimplemented public operations forwarded to a running API return 404.
If the destination is unavailable, YARP returns a proxy error rather than a
successful placeholder response.

## Gateway routing

Original paths, methods and queries are preserved; there is no prefix removal.

| Owner | Public paths forwarded |
|---|---|
| Identity | /api/v1/auth/**, /api/v1/users/**, /api/v1/audit-logs/**, /.well-known/jwks.json |
| Delivery | /api/v1/quotes/**, /api/v1/parcels/** (including future SSE), /api/v1/deliveries/**, /api/v1/couriers/**, /api/v1/zones/**, /api/v1/pricing-tiers/**, /api/v1/reports/deliveries, /graphql |
| Merchant | /api/v1/merchants/**, /api/v1/merchant/orders/**, /api/v1/merchant/webhooks/** |

No gateway route matches `/internal/**`, gRPC service paths or Worker. Delivery
forwarding explicitly disables response buffering for future SSE support.
Forwarding configuration does not implement or authorize any feature. Authentication
and resource ownership checks must be implemented before protected handlers.

## Configuration and infrastructure boundaries

ASP.NET Core defaults apply: appsettings.json, environment-specific appsettings,
Development user secrets, environment variables, then command-line overrides.
Nested environment keys use double underscores. No custom local JSON loader is
installed. `config/local.example.json` is a sanitized reference, not a loaded
configuration file; its empty credentials must be supplied privately when their
adapters are introduced. Never copy every workload's database secret into one host.

Listener overrides use `Kestrel__Endpoints__Http__Url`; Delivery's HTTP/2 listener
uses `Kestrel__Endpoints__Grpc__Url`. Explicit Kestrel endpoints take precedence
over `ASPNETCORE_URLS` / `--urls`. For future containers set HTTP to
`http://0.0.0.0:8080`, Delivery HTTP/2 to `http://0.0.0.0:8081`, and Gateway
HTTP to `http://0.0.0.0:8080` on the trusted private network. Only Gateway should
be published as loopback host port 5000. Base YARP destinations use the canonical
`identity-service`, `delivery-service`, `merchant-service` DNS names on 8080;
Development overrides them with the loopback addresses above. The Worker health
listener and Delivery gRPC listener stay private.

The planned PostgreSQL deployment has one instance and four privately owned
logical databases with separate credentials, never a shared schema or cross-service
query. RabbitMQ/MassTransit belong to later messaging integration. No PostgreSQL,
RabbitMQ/MassTransit, GraphQL, gRPC or SSE packages/adapters are installed in this
checkpoint. There are no migrations, generated clients, event publishers or consumers.
Docker/Compose packaging and dependency-aware readiness remain future work.

Do not add Redis, SignalR, a separate realtime process or extra capability services.
See [CONTRIBUTING.md](CONTRIBUTING.md) for review and contribution conventions.
