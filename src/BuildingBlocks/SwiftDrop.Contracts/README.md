# SwiftDrop.Contracts

Dependency-free integration contract library. No entities, persistence, authorization,
pricing, state transitions, shared application handlers or cross-service database access.

The V1 folders contain design assets copied unchanged from the documentation baseline
[233f707](https://github.com/SwiftDrop-Group09/swiftdrop-docs/tree/233f7077f54949c0cacc31f11b203bf4a27d02b4/api/contracts).
They are not generated C# clients, message consumers, publishers or gRPC implementations.

- `Events/V1/status-changed-v1.schema.json`: StatusChanged v1 wire schema.
- `Grpc/V1/delivery.proto`: internal Merchant-to-Delivery service definition.

Future C# message types belong under `SwiftDrop.Contracts.Events.V1`; generated gRPC
types should use `SwiftDrop.Contracts.Grpc.V1`. Keep transport messages separate from
each API's domain models. Additive compatible changes retain a version; breaking
changes require a new explicit version and consumer migration. Review proposed
contracts with their owners before generating code or introducing transport packages.
