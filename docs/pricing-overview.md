# Admin pricing overview

`GET /api/v1/pricing-tiers/overview` is Admin-only. Page defaults to 1;
pageSize defaults to 20 and permits 1-100. An optional zoneId scopes the read
to a single zone. Unknown/repeated query keys, empty/invalid zone IDs and
invalid/overflowing pagination fail with 400.

The response counts and pages delivery zones by name then ID, including zones
with no tariffs. Rows contain zoneId, zoneName, isZoneActive, tierCount (all saved
versions) and activeTiers (existing versioned DTOs). Missing sizes mean no active
tier; an inactive zone can retain active pricing. Zone count, rows, tier counts
and active rates use one RepeatableRead snapshot. Only the selected page's zones
are used for the pricing queries. Historical versions are fetched separately
using the existing zone-scoped tier endpoint.

No schema migration, seed changes or writes are required. Existing write
transactions, single-active-version rules, price snapshots, concurrency checks
and audit records remain unchanged. Deploy this read endpoint with the frontend
pricing matrix.
