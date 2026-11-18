# Dispatcher parcel zone filtering

`GET /api/v1/parcels` accepts an optional `zoneId` UUID in addition to status,
search, UTC date bounds and pagination. Dispatcher/Admin clients use
`status=Created` for the unassigned queue and can combine all filters.

```http
GET /api/v1/parcels?status=Created&zoneId=11111111-1111-4111-8111-111111111111&page=1&pageSize=20
```

Omitting `zoneId` returns the authorized result across zones. Blank, malformed,
zero and repeated values return HTTP 400. A valid UUID with no matching records
returns HTTP 200 with an empty page. Authorization runs before input validation.

The filter uses persisted `Parcel.ZoneId`, the delivery zone recorded at booking.
It is combined with the existing ownership/status/search/date predicates before
both counting and pagination. Count and rows share the existing repeatable-read
transaction and newest-first ordering by creation timestamp and parcel ID.
Customer requests stay owner-scoped when a zone is supplied. Existing clients
can continue omitting the new field, and the parcel response shape is unchanged.

The Dispatcher selector uses the existing operations-authorized
`GET /api/v1/zones/summary` for zone IDs and names. That response includes inactive
zones and configured zones with no parcels. Administrative zone-list and
configuration permissions retain their existing guards.

## Integration and verification

Release restore/build and all 1,874 backend tests passed on Linux with .NET SDK
10.0.301 and isolated PostgreSQL 18.6. The tested patch is based on backend
`2d67f5fa15db456f294e20e9d60bdba20c28649a`. New application and HTTP/persistence
tests cover invalid and duplicate values, missing filters, unknown zones,
delivery/pickup zone differences, mixed statuses, combined date/search filters,
page counts and ordering, Customer ownership and operations zone options.

The foundation script passed its 13-project dependency/cycle audit. Its startup
probes stopped before launching hosts because the existing stack occupies the
script's fixed listeners. The infra runner successfully built the Delivery and
web images. Production frontend checks with isolated API fixtures passed at
1280px, 390px and 320px, including Select keyboard use, filter submission and
Light/Dark/Device appearance. The synced infra runner applies shared migrations
during `up`; shared migrations remain deferred, so Docker startup and
shared-environment TC-D01 acceptance remain pending. No shared database migration
or schema change is included in this feature.

Roll out this Delivery read change before the frontend zone-filter UI. The
frontend handoff is in `swiftdrop-frontend/docs/dispatcher-integration.md`.
The proposed central REST `ParcelQuery` documentation needs the optional
`zoneId` field incorporated in the documentation repository's next contract review.
