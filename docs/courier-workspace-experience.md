# Courier workspace implementation

15 November 2026. Feature branch: `feat/courier-workspace-experience`.

The Courier uses the existing Identity session. `GET /api/v1/couriers/me/summary`
returns server-scoped service-day totals and separate overdue work. The default
date comes from `Operations:TimeZone` (Asia/Colombo). Assignments support exact
stage, tracking search and overdue-only filters before paging. History supports
Delivered/Failed, original service date and tracking search. Terminal history remains a minimal receipt. A separate owner-scoped history detail
read adds route cities, parcel facts, service/transition dates, canonical outcome
and a proof-recorded flag, without reopening private Courier detail access.

The summary counts each owned parcel once. Current active ownership and retained
Delivered/Failed ownership are separate; reassigned-away rows do not disclose or
count the replacement's outcome. Summary and paginated reads use repeatable-read transactions; terminal detail
uses one authorized projection. Invalid/duplicate/unknown filters are rejected after authorization.

The frontend retains exact versions, immutable attempts, idempotency keys and
proof bytes for an uncertain request. Optional notes, required canonical failure reasons, optimistic
versions and proof requirements remain enforced by the existing write handlers.

## Merchant connection and privacy

The existing order processor previously had only Fake/Unconfigured Delivery
clients. `DeliveryClientMode.Grpc` now uses the documented two RPCs. Each call
requires a private service credential; browser JWTs, public Merchant API keys and
caller-supplied role headers do not grant this access. Business IDs originate
from the stored, authorized Merchant job. The transport is private and must not
be routed through the public gateway. Production requires HTTPS. HTTP/2 without
TLS is available only in Development/Testing with explicit options on both sides.

Create validates structured addresses, both active zones, exact weight (up to
three decimal places), destination-zone tariff and accepted pricing version.
The advisory transaction lock serializes create/replay decisions per business.
A durable binding retains external order, key, payload hash and parcel reference
without expiry. It commits with parcel, price, history and outbox. Changed bytes
or a reused identity return a conflict; older parcels without a binding must be
reconciled through lookup, never silently adopted or recreated.

`BookingResult.failure_reason` is an optional protobuf field (11). It contains
only RecipientUnavailable, AddressUnreachable, Damaged or Other for Failed.
Merchant stores this nullable projection, can filter `parcelStatus=Failed`
before paging and displays reason/time/lastRefreshedAt. Same-version enrichment
can fill an old missing reason without regressing status. Older terminal failed
jobs remain eligible for refresh until that category arrives. Courier notes,
recipient details and proof are never returned through the RPC or OrderJob.

Customers use their existing owner-authorized detail/history to see failure
reason/time. Merchant recipients gain no implicit Customer ownership.

## Schema and local configuration

Both migrations are additive; they do not reset existing data:

- Delivery: `MerchantBookingReplayBindings`, a retained table and indexes; the
  existing `delivery_app` role receives SELECT/INSERT when it exists.
- Merchant: `MerchantDeliveryFailureReason`, a nullable category plus constraint.

Apply through the existing one-shot migration commands or development runner.
This feature does not apply changes to live databases during validation.

For separate `dotnet run` terminals, generate one private credential and store
the same value for both projects using .NET user secrets:

```powershell
$rpcBytes = New-Object byte[] 32
$rpcGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
$rpcGenerator.GetBytes($rpcBytes)
$rpcGenerator.Dispose()
$rpcKey = ([BitConverter]::ToString($rpcBytes)).Replace('-', '')
$delivery = 'src/Services/Delivery/SwiftDrop.Delivery.Api'
$merchant = 'src/Services/Merchant/SwiftDrop.Merchant.Api'
dotnet user-secrets set 'MerchantIntegration:ServiceKey' $rpcKey --project $delivery
dotnet user-secrets set 'MerchantIntegration:AllowInsecureTransport' 'true' --project $delivery
dotnet user-secrets set 'Merchant:Orders:Delivery:Client' 'Grpc' --project $merchant
dotnet user-secrets set 'Merchant:Orders:Delivery:Address' 'http://127.0.0.3:8081' --project $merchant
dotnet user-secrets set 'Merchant:Orders:Delivery:ServiceKey' $rpcKey --project $merchant
dotnet user-secrets set 'Merchant:Orders:Delivery:AllowInsecureTransport' 'true' --project $merchant
Remove-Variable rpcKey, rpcBytes, rpcGenerator
```

The infra host runner instead accepts `MERCHANT_DELIVERY_SERVICE_KEY` in its
ignored `development/.env` or exported environment, and passes it only to these
two services. No `VITE_*` secret is introduced. Production/container deployments
need a private HTTPS endpoint and certificate configuration; the development
runner's cleartext option is not a production transport configuration.

## Existing integration boundary

Merchant registration/admin approval, scoped API-key intake and webhook
subscriptions/delivery remain their owning service features. Status reconciliation
does not publish webhooks or replace the existing outbox/event delivery pipeline.
An operational event relay/consumer is still needed to deliver every intermediate
status transition to Merchant webhooks. OAuth client credentials are not added;
the assignment's API-key alternative is already implemented.

## Verified results

Release solution build: zero warnings/errors. All 2,084 solution tests pass:
Delivery 1,217; Merchant 383; Identity 409; Gateway 70; Notification 5. Coverage
includes real HTTP/2 credential refusal, scoped failure reads, exact decimal and
Int64 mapping, concurrent create/replay, atomic booking records, pre-page queue
filters, terminal privacy and same-version failure enrichment. Persistence tests
use disposable local databases; no live project schema/data is changed.

## Loading the updated host API

Restart the development host after building these changes. The new history dialog
uses `GET /api/v1/couriers/me/history/{id}`. An older running Delivery process
returns a routing 404 and the client cannot distinguish that from an unavailable
history resource. Restart through the host runner, then refresh the browser.
No production server or live database was restarted/modified during verification.
