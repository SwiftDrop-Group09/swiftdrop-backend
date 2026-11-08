# PR integration review ? 9 November 2026

Courier PR #12 was reviewed against current SRS, authorization, state-machine and
API contracts, with current dev and the existing Merchant milestone integrated.

The two PostgreSQL deletion tests now accept the two PostgreSQL constraint codes
that can enforce the documented invariant and explicitly verify that the courier
and its referenced membership/assignment remain persisted after rejection.

Customer booking now has GET /api/v1/quotes/zones: Customer-only active zone IDs
and names, ordered by name/id with private/no-store caching. Administrative zone
configuration remains Admin-only. Authorization precedes lazy storage resolution;
unavailable storage returns 503. Tests cover inactive exclusion, minimal fields,
authorization, cache policy and missing database configuration.

Validation: pinned SDK Release restore/build passed with zero warnings/errors.
All 1,453 tests passed: Identity 369, Delivery 942, Gateway 68, Merchant 69 and
Notifications 5. These use isolated PostgreSQL fixtures. Human JWT integration
for Delivery/Merchant, live object storage and broker delivery remain separate
acceptance gates; no production authentication bypass is added.
