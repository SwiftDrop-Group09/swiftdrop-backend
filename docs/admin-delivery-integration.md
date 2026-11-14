# Delivery administration integration

Work branch: `fix/admin-delivery-integration`, based on `dev`.
Reviewed and incorporated `origin/feat/admin-delivery-config-reports`
(`341dd06`) without modifying its source branch.

## Reconciled behavior

- Preserve the existing Identity JWT verifier and service boundaries. Zone writes,
  pricing administration and delivery reports require Admin access; the zone
  summary also permits Dispatcher access.
- Persist zone and pricing concurrency versions using PostgreSQL `xmin`.
  Reject stale writes instead of silently overwriting another administrator.
- Serialize catalog changes, validate postal-code overlap, and write audit
  records in the same transaction as the configuration change.
- Retain historical tariff economics. Activating a version retires the previous
  active tariff for that zone and parcel size. A filtered unique index enforces
  one active tariff per pair.
- Replace the source branch's empty migration with
  `20261112135924_DeliveryConfigAuditPersistence`. It creates the audit table,
  indexes and a trigger rejecting audit updates/deletes. It preserves existing
  quotes, parcels, zones and pricing rows.
- Return controlled unavailable/conflict responses without exposing database
  exceptions. Reports use persisted terminal transitions and UTC ranges; empty
  data produces zero totals.

## Sri Lanka catalog

The Delivery database contains 25 district zones and 75 active LKR tariffs for
Small, Medium and Large parcels, version `LK-2026-11`. District names are plain
operational names. Setup does not insert artificial bookings or customer records.
Existing active prices and administrator deactivations are preserved on reruns.
Postal-code coverage is not invented; administrators can configure it explicitly.

## Verification

Added PostgreSQL integration coverage for persisted versions, stale-write
rejection, atomic tariff replacement, concurrent postal overlap, retained audit
records and endpoint authorization. Updated tariff persistence tests to retire
the previous active version before adding its replacement. Frontend transport
tests and browser checks are recorded in the frontend integration document.

Final validation: all 996 Delivery tests pass. The other solution projects pass
609 tests (Identity 369, Merchant 167, Gateway 68, Notifications 5). The Release
solution build reports zero warnings and errors.

The additive migration was applied to the configured Neon Delivery database on
2026-11-12. All six migrations are applied. Runtime-role reads verify 25 zones,
75 active tariffs, zero parcel records and successful administration/report
queries. Existing catalog rows were retained (zero zones or tariffs added on
this migration run). Colombo to Kandy, Medium, 2.5 kg prices at LKR 795.00.

The integration is committed and merged into `dev`. Source and integration
branches are removed after the remote merge is verified.
