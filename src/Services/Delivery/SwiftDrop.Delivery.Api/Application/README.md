# Delivery Application

Own use cases and ports. Depend on Domain; keep REST, GraphQL and internal gRPC adapters thin and reuse these handlers when implemented.

Namespace: `SwiftDrop.Delivery.Api.Application`.
This folder reserves the boundary; no business implementation is present.
These are folders within one project; dependency direction is a review convention,
not compiler-enforced project isolation.
