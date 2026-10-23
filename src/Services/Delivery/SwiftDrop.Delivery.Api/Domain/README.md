# Delivery Domain

Own this service's entities, value objects and business rules. Keep it independent of Application, Infrastructure, HTTP and external transports.

Namespace: `SwiftDrop.Delivery.Api.Domain`.
The Phase 1 booking model contains owner-bound single-use quotes, versioned
pricing/zone references, Customer or Merchant parcels starting Created/version 1,
immutable accepted-price values, initial history, durable idempotency responses,
and StatusChanged v1 outbox records. It has no EF, ASP.NET, or service dependency.
Factories are persistence/domain primitives; they do not authenticate callers or
constitute the confirmed booking application workflow.

Quote input is retained as a JSON object. Parcel addresses/contact values retain
the ER's text representation; pickup contact is nullable because QuoteInput has
no sender contact. The selected tariff zone is deliveryAddress.zoneId; later
application adapters must use it consistently for the parcel and price snapshot,
while preserving/validating both addresses. No operational tariffs, surcharge or
tracking generator is introduced here. Application validation and pricing are
later phases. Monetary values are already rounded to two decimals before being
stored; fee calculation must use the documented half-up rule.

These are folders within one project; dependency direction is a review convention,
not compiler-enforced project isolation.
