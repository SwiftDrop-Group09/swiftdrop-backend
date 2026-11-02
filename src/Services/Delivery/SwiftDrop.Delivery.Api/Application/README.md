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
operational tariff seeds are included. Quote creation persists only a quote.

Phase 3 adds `Bookings/CustomerBookingHandler`. It accepts only quoteId and
acceptTotal plus an idempotency key, authorizes the Customer before storage,
reconstructs the retained quote details and verifies its fingerprint and exact
retained price. Identical committed requests return the stored 201 body and
Location before quote expiry/consumption checks. New confirmations create a
parcel, snapshot, initial history and pending outbox row, consume the quote and
complete the replay record in one transaction through `IBookingStore`.

Phase 7 `BookingAssembler.CreateCustomer` and `CreateMerchant` provide separate
creation contracts over the same domain factories, tracking and price/history/
outbox assembly. Customer history derives its actor from its owner. Merchant
requires an explicit trusted audit actor UUID and an accepted version matching
the validated tariff. The shared `Create` method checks retained price, zone,
size and initial state. A future Merchant adapter must apply its own
service/merchant permissions and input/active-zone/tariff checks before using it.
The [Member 7 handoff](../../../../../docs/delivery-merchant-booking-handoff.md)
records the exact wire mapping and transaction/reconciliation responsibilities.
`ITrackingNumberGenerator` supplies the random public code. HTTP adapters and
persisted quote validation share the exact-decimal converter in
`Application/Serialization`; input precision is unchanged.

Application has no EF, HTTP context, broker or Identity DB dependency.

`ICurrentCustomer` represents an identity established by a trusted entry adapter.
Tests provide that identity only in test infrastructure. Production presently
fails closed until actual Identity JWT verification is integrated.
These are folders within one project; dependency direction is a review convention,
not compiler-enforced project isolation.
