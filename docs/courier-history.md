# Courier historical deliveries

FR-R05 / TC-R05 / Phase 6 implement the documented **GET
/api/v1/couriers/me/history**, operation CourierHistory. It is a Courier-self read,
using the stable UUID sub from a trusted authenticated identity. No caller ID,
forwarded identity header, Merchant credential or Admin override selects a Courier.

Only the documented PageQuery is accepted:

| Parameter | Rule |
|---|---|
| page | Positive integer; default 1 |
| pageSize | Integer 1-100; default 20 |

Unknown/duplicate parameters, invalid integers and overflowing offsets return 400
after identity/role checks. No status, date/from/to, service day, includeOverdue,
cursor or courierId query parameter is invented. The endpoint applies the SRS's
fixed Delivered/Failed filter and historical ownership itself.

The 200 ParcelPage envelope contains items, totalCount, page and pageSize. Each
item uses the established six common fields:

```json
{
  "id": "00000000-0000-4000-8000-000000000001",
  "trackingNumber": "illustrative-tracking-code",
  "status": "Delivered",
  "version": 6,
  "priceSnapshot": {
    "total": 540.00,
    "currency": "LKR",
    "pricingVersion": "illustrative-version"
  },
  "createdAt": "2026-10-28T10:00:00+00:00"
}
```

Example values are synthetic. Status is canonical Delivered or Failed; createdAt
is parcel creation time, not a renamed completion timestamp. Tracking, accepted
price and version come from retained Delivery records. No new terminalAt field is
added. An empty page is items=[] with totalCount=0; beyond the last page, items
is empty but totalCount still reflects all matching records.

## Historical ownership and privacy

Terminal writes release the Active assignment slot. History therefore checks the
caller’s retained Delivered or Failed assignment, matching parcel status, rather
than current Active ownership. A parcel reassigned from A to B and completed by B
appears only for B. A's Reassigned row does not authorize B's later delivery outcome.
If a parcel is assigned back to A and A actually fails/completes it, A receives one
record despite multiple old assignments. Cancelled, active and never-assigned work
are excluded. The API does not infer terminal ownership from the latest timestamp.

This historical summary permission is distinct from private parcel detail/history
access. Closed Couriers still cannot use GetParcel to read contact/address/private
audit data merely because they see a terminal summary. The collection returns no
recipient name/phone, addresses, previous/current Courier IDs, Customer/Merchant
IDs, service dates, failure reasons/notes, proof metadata/keys/URLs, replay records,
or outbox data. Those columns are not selected by the history SQL query. The
existing owner/operations/private-detail and proof-download policies are unchanged.

Results order by parcel createdAt descending, then id descending, matching the
existing collection convention. Count and page use one PostgreSQL read-only
repeatable-read snapshot. A completion/failure committed between those queries
is absent from both until the next request. Separate page requests may see later
commits; this is not an undocumented cursor or a snapshot spanning multiple HTTP
requests. No business timezone, current service day or storage/broker availability
is required to read historical records.

## Errors and integration

All responses are private/no-store and use the existing read ProblemDetails with
safe code/correlationId. Missing/invalid identity is 401; missing Courier role is
403 (including Dispatcher/Admin); invalid PageQuery is 400; read dependency failure
is 503. A valid Courier with no historical assignments gets a normal empty 200.
Cancellation propagates without retrying under another actor. Reads do not write
parcel versions, assignments, status history, proof, keys or outbox rows.

No migration or new credential is required. Existing Delivery migrations/indexes
and immutable price snapshots are reused. Production still registers fail-closed
UnconfiguredIdentityHandler pending real Identity JWT/JWKS integration; positive
tests use isolated trusted principals. History provisioning comes from the existing
dispatcher/status/proof flows, not a new allocation/provisioning API.

The canonical docs specify PageQuery/ParcelPage groups without an exact history
projection or an explicit reassigned-away example. This implementation follows
FR-R05's *their completed and failed records* and the private-detail resource guard:
minimal terminal-actor summaries, without extending historical contact access.
Broader filters/private history fields require a separately verified contract.

React/Flutter Courier history screens remain Phase 7. Live Identity, Neon deployment,
dispatcher integration and outstanding mandatory proof/broker/container acceptance
remain dependencies for the overall workflow; this Should endpoint does not make
them complete. See the [Phase 6 report](courier-phase-6-assessment.md) for actual
tests, source scope and remaining integration limitations.
