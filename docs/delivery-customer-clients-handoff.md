# Customer quote and booking integration handoff

**Pending implementation for React and Flutter.** This Phase 6 handoff is based
on backend d814089 and docs d97fff4, reinspected on 24 October 2026. The backend
checkpoint passed; neither Customer client currently implements the interaction.
See the [Phase 6 assessment](delivery-phase-6-assessment.md) for exact blockers
and executed checks. No client feature test or end-to-end success is implied.

## Required shared dependencies

React c062b11 supplies Router, Query, React Hook Form/Zod, Tailwind and testing
conventions, but its session, transport and Customer feature directories are
empty. Flutter e9778c1 is the counter scaffold; its documented core/network,
session, configuration and navigation layers are examples awaiting implementation.

Before wiring the feature, the shared client foundations must supply:

- An authenticated Customer session, stable internal user identity and a
  token-aware HTTP client. Booking consumes the session; it does not implement
  login, refresh, token storage, a manual bearer-token field or another session
  manager. Session changes must invalidate private UI and cached responses.
- The established route/app integration and a public Gateway base URL. React
  uses VITE_API_BASE_URL=/api: append /v1/quotes and /v1/parcels once. Flutter's
  API_BASE_URL example is not yet read by code; its future base/path convention
  must resolve exactly /api/v1/quotes and /api/v1/parcels, without duplicate prefixes.
- An authorized address/eligible-zone source providing both zone IDs. The
  documented GET /api/v1/zones is Admin-only and unimplemented. Do not call it
  as Customer, broaden its permissions, invent geographic IDs, embed fee tables
  or expose technical UUID entry as the normal address workflow. A trusted
  host-supplied catalog or reviewed Customer endpoint needs an agreed contract.
- Lossless numeric JSON decoding/encoding for quoted totals and submitted
  decimal values. Neither client currently supplies this transport behavior.
- The matching runtime/tools and Flutter app-wide decisions identified in the
  assessment. No new global architecture/package choice is made by this handoff.

Real Identity is not yet available: normal Delivery hosts return 401, and
registration does not issue a token. Follow the
[Identity integration handoff](delivery-identity-integration-handoff.md).
Do not embed backend test principals in a client or use X-User-* as credentials.
The proposed browser/mobile credential storage and refresh policies remain with
the shared authentication implementation; no secret belongs in VITE_* or
dart-define values.

## Verified wire contract

Sources are the current
[REST contract](../../swiftdrop-docs/api/rest-api.md),
[booking use case](../../swiftdrop-docs/requirements/use-case-specifications.md),
[QuoteContracts](../src/Services/Delivery/SwiftDrop.Delivery.Api/Application/Quotes/QuoteContracts.cs),
[quote validator](../src/Services/Delivery/SwiftDrop.Delivery.Api/Application/Quotes/QuoteInputValidator.cs),
[BookingContracts](../src/Services/Delivery/SwiftDrop.Delivery.Api/Application/Bookings/BookingContracts.cs)
and the actual endpoint/problem implementations. Client authorization is for
usability; the server remains authoritative for identity, ownership, pricing,
quote freshness and idempotency.

| Operation | Exact request | Success |
|---|---|---|
| Estimate | POST /api/v1/quotes, application/json, authenticated Customer; body contains pickupAddress, deliveryAddress, packageDetails, recipient | 200 Quote; only a quote is created |
| Confirm | POST /api/v1/parcels, application/json, same authenticated Customer; one Idempotency-Key header; body contains only quoteId and acceptTotal | 201 committed Parcel projection plus Location |

Each address contains **street, city, postalCode, zoneId**. Each text value is
required/nonblank and must not contain NUL. Each zoneId must be a nonempty UUID;
the server independently checks that both zones are active/eligible. Recipient
contains **name, phoneNumber**, with the same required-text checks. No stricter
phone/postal pattern or text-length limit is specified by the current quote
validator; do not silently invent a business restriction.

Package details contain **weightKg** as a JSON number greater than zero and at
most 30, and **size** exactly Small, Medium or Large. Do not silently round
invalid/excess-precision input into a different valid weight. Keep form errors
near labelled fields and send only the documented fields. Caller-supplied
userId/ownerId/merchantId/roles and unknown properties are rejected.

The delivery address zone selects the tariff; pickup-zone eligibility is checked
separately. Pricing is server-owned. Do not calculate a fee or cross-zone
surcharge in either client.

| Response | Exact fields |
|---|---|
| Quote | quoteId UUID, total numeric decimal, currency string (currently LKR), pricingVersion string, expiresAt UTC timestamp, fingerprint opaque string |
| Initial Parcel | id UUID, trackingNumber string, status Created, version 1, priceSnapshot object, createdAt UTC timestamp |
| priceSnapshot | total numeric decimal, currency string, pricingVersion string |

The quote expires ten minutes after creation. Display the server total/currency
and expiry; the API supplies no baseFee/weightFee breakdown. Treat fingerprint
and pricingVersion as opaque values. An unexpired retained quote can remain
valid after tariff activation changes; do not locally reprice it or reject it
because a newer version exists.

Confirmation sends **quoteId** and **acceptTotal** equal to the quoted numeric
value. Do not send addresses, recipient, fingerprint or pricingVersion again.
Idempotency-Key must be one nonblank value, at most 128 characters, with no
control characters. Generate a random opaque key once per explicit attempt.

Only a valid committed 201 response supplies booking success and the tracking
number. Location is /api/v1/parcels/{id}, but that GET is not implemented today.
Display the returned result without requiring an invented tracking read or
creating a local tracking number. No additional private fields are returned by
the current booking projection.

## Decimal preservation

The backend accepts nonnegative monetary values below 10000000000000000 with
at most two fractional digits, within numeric(18,2). Valid amounts can exceed
the cents precision of JavaScript Number and Dart double. For example,
90071992547409.93 is a transport test vector, not an operational tariff.

Preserve the quoted decimal's exact numeric value through response decoding,
preview and acceptTotal serialization. Parsing through a lossy double and then
constructing a decimal cannot recover lost cents. Use a reviewed lossless
number parser/codec or equivalent exact transport; test it before integration.
JSON numeric strings are not accepted by the backend's strict decimal converter.
Do not round, recalculate fees or silently narrow the server's supported range.

## Interaction and retry state

| State / event | Required behavior |
|---|---|
| Editing | Collect both addresses, recipient and package details; validate usability before requesting a quote |
| Estimate pending | Show loading and prevent duplicate estimate actions; discard stale responses after input revision or session change |
| Quote preview | Show server total/currency/expiry and a separate explicit confirmation action; no parcel POST yet |
| Decline or edit before submission | Discard the preview; any retained-input edit, including recipient, needs a fresh estimate; decline makes zero parcel requests |
| Expired before first confirmation | Require a fresh quote and explicit acceptance of the new total |
| Confirm pending | Freeze owner, quoteId, exact acceptTotal and key for this attempt; prevent duplicate click/tap submissions synchronously as well as visually |
| Unknown outcome | Retain that immutable attempt and offer bounded same-key/body recovery; do not create a new key/quote or display a failed/successful parcel as a certainty |
| Committed 201 | Show the server result/tracking number; repeated identical retries resolve to the same result |

Timeout, network loss, malformed/unreadable success response, HTTP abort or a
booking 503 may follow a successful database COMMIT. Back navigation or widget
disposal does not cancel a committed booking. Keep the same attempted payload
and key, scoped to the same Customer, and retry it **even if the local quote
timer has expired**. The server checks committed replay before quote expiry or
consumption. A new quote/key while that outcome is unknown can create a second
parcel and must not be an automatic recovery action.

Clear private preview/result/cache state on logout or principal change, cancel
client observation where supported and discard stale callbacks. Never show A's
result or replay A's pending attempt as B. Token refresh for the same stable
Customer must preserve the original attempt. The shared app/session foundation
must define privacy-safe retention/recovery across navigation and restart;
booking must not invent token/session storage to solve it.

## Server errors and recovery

Use safe ProblemDetails code/status/field errors and X-Correlation-ID. Do not
render raw exception bodies, log addresses/phone/tokens or infer failure meaning
only from a localized title. Preserve user input where appropriate.

| Status / code | Client behavior |
|---|---|
| 400 validation | Show the supplied safe field/body/key errors; correct input before a new estimate/confirmation |
| 401 unauthorized | Delegate reauthentication to the shared session. No fake identity fallback; retain an uncertain attempt only for its original owner |
| 403 forbidden | Explain that Customer access is required; headers or another role cannot elevate the session |
| 404 not_found | Quote is missing or not owned; do not reveal another user's data or claim successful reconciliation |
| 422 unsupported_zone | Recheck the approved address/zone selection; do not invent coverage |
| 503 pricing_unavailable / quote-storage failure | Keep quote input and allow a safe estimate retry; do not fabricate a price |
| 503 delivery_unavailable / booking network failure | Booking outcome may be unknown; retry the original confirmation/key |
| 409 operation_in_progress | Respect Retry-After (currently 1 second) with bounded same-key/body retries and user feedback |
| 409 quote_expired / price_mismatch | On a definite server rejection, obtain a fresh quote and require explicit reconfirmation; never silently accept a changed total |
| 409 quote_consumed / quote_conflict | Stop automatic creation and surface the conflict; do not assume a new key is safe |
| 409 idempotency_conflict | The key/body pairing differs; stop and resolve the original attempt, never mutate its body under that key |
| 409 key_expired | The 30-day replay window ended; do not treat it as permission to create another booking automatically |

The Customer price code is **price_mismatch**, not the separate Merchant
pricing_changed code. A local expiry indication is not a definite server
rejection of an already submitted confirmation.

## Feature boundaries after prerequisites are supplied

These are proposed feature locations, not newly created infrastructure:

- React: app/portals/customer/booking.tsx and its app/routes.ts registration;
  app/features/customer-booking/ contracts, validation, endpoint functions,
  Query mutations/hooks, form, quote preview, result and focused tests. Reuse
  RHF/Zod/Query and the future shared session/native-fetch transport. Keep
  route splitting, ~/ imports, SPA-safe initial rendering, responsive layout,
  labels, keyboard use, focus and accessible loading/error feedback. Other
  portals and the app-wide authentication implementation stay separate. Retain
  the existing disabled automatic mutation retries; any confirmation recovery
  must explicitly preserve the original attempt and be bounded.
- Flutter: lib/features/customer/booking/{data,domain,presentation}/ and mirrored
  test/features/customer/booking/ tests. Consume the agreed app/router/state,
  core/network/session and configuration boundaries. Keep API parsing out of
  widgets; do not introduce another global state/session manager or alter Courier.
  Retain Android minSdk 26 and the current loopback/debug-only HTTP or configured
  TLS policy. A dart-define example alone does not implement URL configuration.

Only fields returned by Delivery may determine displayed price and booking
success. Merchant ingestion, Courier features, tracking/history/SSE, backend
zone authorization changes and Identity implementation are separate scopes.

## Pending client acceptance matrix

**None of the following feature tests has been implemented or executed.** Run
the equivalent focused unit/component/widget cases in both clients, followed
by each repository's full checks and real authenticated integration.

| Case | Required observation |
|---|---|
| Form validation | Required/blank/NUL fields, both zones, exact size and weight boundaries show usable errors; invalid input makes no estimate request |
| Exact payload | Only documented quote fields are sent; confirm sends only quoteId/acceptTotal plus one key; URL prefixes are correct |
| Quote preview | Server amount/currency/expiry shown without computing fees; quote action makes zero parcel calls |
| Decline | Declining an unsubmitted quote creates no parcel and sends no confirmation |
| Input edit / stale estimate | Changing any retained input invalidates preview; an older response cannot restore the previous quote |
| Explicit confirmation | Separate consent sends one confirmation; success waits for valid 201 and displays server tracking/snapshot |
| Duplicate submit | Rapid repeated click/tap uses one active attempt and key; no second booking |
| Expiry / repricing | Unsubmitted expiry and definite server price/expiry rejection require a new preview and explicit consent; retained valid quotes are not client-repriced |
| Lost response / 503 | Original owner/key/exact body is retried, including after local expiry; final result/event remains singular |
| In-progress conflict | Retry-After respected with bounded retries; original key/body retained |
| Abort / unreadable 201 | Remains an uncertain outcome, not an automatic new attempt or fabricated success |
| Other failures | Safe handling for 400/401/403/404/409/422/503 and network failures; preserve useful form data and never show raw private payloads |
| Numeric boundaries | Exact large monetary values, decimal weights and relevant rounding boundaries survive decoding and re-encoding; no numeric strings or lossy-double round trip |
| Session changes | Shared refresh retains same-owner attempt; logout/new user clears private caches/UI and cannot receive stale results or retry another owner |
| Client usability | React responsive/keyboard/label/focus checks; Flutter widget lifecycle/navigation and API-26+ device behavior |
| Actual integration | Real signed-token sessions plus provisioned zones/pricing complete quote/decline/confirm/retry in both clients through Gateway; evidence is separate from mocked transport tests |

React's repository check is npm run check after using the pinned tools and
npm ci. Flutter requires dart format lib test, flutter analyze and flutter test,
then the documented Android/device integration checks. No hosted CI, container
startup, device run or real token interoperability is established by this handoff.
