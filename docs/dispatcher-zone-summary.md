# Dispatcher zone summary

`GET /api/v1/zones/summary` is a private Dispatcher/Admin read for FR-D05 and
TC-D05. The endpoint policy and application handler both enforce operations
access. Other authenticated human roles receive 403; anonymous requests receive
401. Authorization precedes query validation. Existing activity auditing remains
enabled and authoritative.

## Date contract and aggregation

Omit both `from` and `to` for all booking dates. This remains the default used by
queue/workload zone catalogs and the Admin dashboard's operational snapshot.
For a filtered summary, supply both ISO 8601 UTC timestamps, ending in `Z` or
`+00:00`, with optional fractional seconds (one to seven digits). `from` is
inclusive, `to` exclusive, `to` must be strictly later, and the interval must not
exceed 366 days. A 366-day interval is accepted. An undated request is not subject
to that range limit.

```http
GET /api/v1/zones/summary?from=2026-11-13T00:00:00Z&to=2026-11-15T00:00:00Z
```

Empty, malformed, date-only, non-UTC, one-sided, equal, reversed and oversized
bounds return HTTP 400 ProblemDetails with code `invalid_date_range`, field
errors and an opaque correlation ID. Duplicate or unknown query parameters
return HTTP 400 `validation`. Previously, malformed timestamps silently became
missing bounds and partial requests were accepted; callers must now omit both
or supply a valid pair. The application validates raw timestamps before the
store is invoked. Delivery reports retain their separate required-bound rules.

The unchanged response is `zones: [{ zoneId, name, statusCounts, total }]`.
Aggregation groups each persisted parcel's **current status** by its delivery
`ZoneId`, filtering **booking `CreatedAt`**, not status-transition time or
assignment service date. Both Customer and Merchant parcels count once.
Configured inactive and empty zones remain present. Missing status keys mean
zero; `total` is the sum of all statuses for that zone, including any additional
status keys. This is not a historical status-event report.

The shared proposed DateQuery documentation does not specify a zone-summary
default. This feature makes the existing all-date default explicit; it does not
adopt the dashboard date default for operations catalogs. The canonical contract
owner should retain this distinction when reconciling the proposed documentation.

## Integration and verification

Deploy the stricter date validation before relying on it from period controls.
There are no schema, migration, persistence-model or response-shape changes.
Frontend period controls must send the midnight after their inclusive Through
day. Undated callers continue to omit both bounds.

Dedicated handler and PostgreSQL/HTTP tests cover all eight lifecycle statuses,
both origins, different pickup/delivery zones, inactive and zero-count zones,
exact booking boundaries, current statuses updated outside the booking period,
totals, all-date defaults, UTC/366-day validation, cancellation forwarding,
role denial and authorization before validation. PostgreSQL uses the existing
private disposable fixture with external fallback unset.

Shared-environment acceptance must compare authorized API/page output against a
known persisted dataset. Historical development-data and missing-schema reports
do not establish current counts or current audit/schema readiness. Keep real
authentication and auditing enabled. Shared migrations and canonical startup
remain deferred under the workspace's existing instruction; do not run `up` as
an incidental check because it applies migrations.

## Local verification — 14 November 2026

Tested the uncommitted feature patch based on
`11bef3f1982b0262280eb3f8bc11b949771e1966` on Linux, .NET SDK 10.0.301 and private
PostgreSQL 18.6. Release restore/build passed with zero warnings/errors. All
2,065 solution tests passed, none skipped, including the 39 dedicated summary
cases. A trailing-newline timestamp regression failed before the parser used
absolute string anchors; both application and HTTP regressions now pass.
The initial full run exhausted the host's inotify watcher instances;
the passing rerun used `DOTNET_USE_POLLING_FILE_WATCHER=1` without changing host
limits. External test-database fallback variables were unset.

The canonical infra runner at `5f5eeb5` built the Delivery image. Foundation
verification passed the 13-project reference/cycle audit, then stopped before
host launch because its fixed listener ports were occupied. Real Docker startup
and shared known-data/identity acceptance remain pending under the migration
deferral. No shared schema, running service or database was changed.
