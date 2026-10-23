# Delivery Application

Own use cases and ports. Depend on Domain; keep REST, GraphQL and internal gRPC adapters thin and reuse these handlers when implemented.

Namespace: `SwiftDrop.Delivery.Api.Application`.
Phase 2 implements `Quotes/QuoteEstimator` through `ICurrentCustomer` and
`IQuoteStore` ports. Authentication/role and required-field checks precede
storage. `PriceCalculator` calculates decimal LKR fees with half-up rounding;
`QuoteCanonicalInput` retains normalized complete input and hashes it together
with the verified owner and retained tariff identity/version/currency.

The destination address zone selects pricing; both supplied zones must be active.
Exactly one active zone/size tariff must exist. Missing or ambiguous pricing
returns unavailable configuration. No version ordering is assumed. No
operational tariff seeds are included. Quote creation persists only a quote;
booking, tracking-number generation, replay orchestration and events are later
handlers. Application has no EF, HTTP context, broker or Identity DB dependency.

`ICurrentCustomer` represents an identity established by a trusted entry adapter.
Tests provide that identity only in test infrastructure. Production presently
fails closed until actual Identity JWT verification is integrated.
These are folders within one project; dependency direction is a review convention,
not compiler-enforced project isolation.
