# Customer delivery summary and private documents

`GET /api/v1/parcels/summary` supplies the Customer dashboard. It requires a
verified Customer principal with a nonempty user ID. Other roles receive 403;
anonymous callers receive 401. It accepts no query parameters, including owner
IDs, pagination or filters. Invalid parameters return 400 after authorization.

The response contains `totalCount`, `activeCount`, `deliveredCount`, `failedCount`,
`cancelledCount`, `activeDeliveries` and `recentDeliveries`. Counts cover every
parcel with the caller's `CustomerId`. Active means every nonterminal status,
including Created. Recent history means Delivered, Failed or Cancelled.
Each list contains at most five existing Customer parcel list items, ordered by
UpdatedAt descending then Id descending. Counts are independent of list limits.

The store executes status aggregation and both bounded lists in one read-only
PostgreSQL repeatable-read transaction. Ownership is in each SQL predicate.
Existing private read metadata applies `private, no-store` caching at HTTP level;
the frontend keeps a short-lived, user-scoped in-memory cache. No schema change
or IAM implementation is required. The existing Gateway parcels route forwards
the endpoint.

## Private proof storage alignment

Production composition now resolves `IPrivateProofReader` to PostgreSQL, matching
the existing `IPrivateProofStorage` writer. The handler still authorizes ownership
or Admin access and delivered status before resolving a private object key.
The reader rejects client-selected URLs/keys and validates content type, stored
length and SHA-256 before returning bytes. Images remain private; object storage
references and payloads are never exposed in list/summary responses.

The PostgreSQL adapter aliases raw SQL columns to their EF projection names and
leaves the DI-owned request DbContext alive across stage, verify and subsequent
transaction operations. This fixes proof lifecycle failures as well as previews.
The legacy S3 implementation remains available in source but is not selected by
the current PostgreSQL composition. This change does not copy, delete or migrate
live proof records.

Tests use a disposable PostgreSQL cluster: HTTP role/owner isolation, all-status
counts, bounded list ordering, empty summaries and rejection of owner query
parameters; proof stage/verify/read/list/delete round trips, invalid keys and
corrupt-content rejection. Existing receipt/proof authorization and Courier
workflow tests remain required.

Validation on 15 November 2026: all 1,188 Delivery tests passed against disposable
PostgreSQL; the full solution Release build completed with zero warnings/errors.
