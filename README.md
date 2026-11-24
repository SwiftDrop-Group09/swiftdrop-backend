# SwiftDrop Backend

SwiftDrop is a parcel delivery platform for customers, merchants, couriers, dispatchers, and administrators. This repository contains the .NET 10 solution, including the public API gateway, three domain APIs, background workers, and versioned integration contracts.

## Service ownership

| Component | Responsibility |
| --- | --- |
| Gateway | YARP routing for public requests. Internal service endpoints stay on private listeners. |
| Identity API | Registration, password and Google sign-in, sessions, profiles, user administration, and JWT signing keys. |
| Delivery API | Pricing, quotes, booking, tracking, dispatcher assignment, courier workflows, and proof of delivery. |
| Merchant API | Merchant onboarding, order submission, and signed webhook delivery with retries and replay. |
| Delivery Outbox Relay | Publishes committed Delivery status events from PostgreSQL to RabbitMQ. |
| Notification Worker | Consumes status events and records notification jobs and provider attempts. |
| Contracts | Versioned schemas and protocol definitions shared across service boundaries. |

Identity, Delivery, Merchant, and Notifications each own their PostgreSQL database. Services exchange data through APIs and events. Delivery validates Identity access tokens against Identity's public JWKS endpoint. Courier proof is stored privately in the Delivery database.

## Run locally

Keep `swiftdrop-backend`, `swiftdrop-frontend`, and `swiftdrop-infra` in the same parent directory. Follow the infrastructure setup to choose the Neon-backed development stack or the isolated local PostgreSQL stack. The web app runs at `http://localhost:5173` and the gateway at `http://localhost:5000`. [Infrastructure setup](https://github.com/SwiftDrop-Group09/swiftdrop-infra/blob/dev/README.md)

For host-side development, install the .NET 10 SDK specified in [global.json](global.json). Store database credentials and signing material in per-project development secrets or the private infrastructure environment file. [Development secrets](docs/development-secrets.md)

`GET /health` checks process liveness. Identity's `GET /ready` also checks the registration store, including database configuration and schema readiness.

## Build and test

Run these commands from the repository root.

```sh
dotnet restore SwiftDrop.sln
dotnet build SwiftDrop.sln --no-restore --configuration Release
dotnet test SwiftDrop.sln --no-build --configuration Release
```

Delivery persistence tests require local PostgreSQL binaries for a disposable cluster or a test database that allows temporary schemas. See the [Delivery persistence guide](src/Services/Delivery/SwiftDrop.Delivery.Api/Infrastructure/README.md). Run [the foundation smoke script](scripts/verify-foundation.ps1) with PowerShell to check process startup, project boundaries, and gateway routing.

## Implementation guides

| Area | References |
| --- | --- |
| Identity | [Cross-service contract](docs/identity-cross-service-contract.md), [Delivery JWT integration](docs/delivery-identity-jwt-integration.md) |
| Customer | [Local booking runbook](docs/customer-booking-local-runbook.md), [Delivery summary and documents](docs/customer-delivery-summary.md) |
| Dispatch and courier | [Assignment](docs/dispatcher-assignment.md), [Courier verification](docs/courier-verification.md), [Proof storage](docs/courier-proof-storage.md) |
| Merchant and messaging | [Merchant orders](docs/merchant-orders.md), [Outbox relay](docs/delivery-outbox-relay-handoff.md), [Status notifications](docs/member-9-status-notifications.md) |
| Administration and assistant | [Delivery administration](docs/admin-delivery-integration.md), [Assistant execution](docs/assistant-execution.md) |
| Deployment and quality | [Container configuration](deployment/README.md), [Operational development data](docs/development-data.md), [Coverage](docs/backend-coverage.md), [Contributing](CONTRIBUTING.md) |

## SwiftDrop repositories

| Repository | Responsibility |
| --- | --- |
| [Backend](https://github.com/SwiftDrop-Group09/swiftdrop-backend) | Gateway, APIs, background workers, and integration contracts |
| [Web frontend](https://github.com/SwiftDrop-Group09/swiftdrop-frontend) | React web application and role-based workspaces |
| [Mobile app](https://github.com/SwiftDrop-Group09/swiftdrop-mobile) | Flutter application for customers and couriers |
| [Infrastructure](https://github.com/SwiftDrop-Group09/swiftdrop-infra) | Local development, Docker Compose, and Kubernetes deployment |
| [Project documentation](https://github.com/SwiftDrop-Group09/swiftdrop-docs) | Requirements, architecture, integration design, and assessment evidence |
