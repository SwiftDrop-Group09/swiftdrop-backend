# Merchant work aligned with dev

The changes previously merged into `main` by PR #11 are preserved on the team's
`dev` integration branch. They add Merchant-owned onboarding, approval and API-key
domain/persistence/API scaffolding; no other service's tables are used.

Review corrected two mismatches with the shared contracts: the repository keeps
its pinned SDK with roll-forward disabled, and newly issued keys contain a full
key ID plus 256 random secret bits and use SHA-256(salt || UTF8(rawKey)). Existing
short-prefix keys retain their original verification until revoked. Raw keys are
returned only at creation; metadata excludes hashes and salt.

This is the same staged Merchant milestone as PR #11, not completion of the full
Merchant SRS. Its default identity adapter intentionally rejects credentials until
the Merchant owner connects a verified JWT adapter. Tests inject principals only
inside the test host. There is no production identity-header or unsigned-token
fallback. Dashboard/order ingestion/webhooks remain separate milestones; their
absence is not replaced with success stubs. No Neon migration was executed during
review, and normal startup still never applies DDL.

Validation on 9 November 2026: restore and Release build passed with zero warnings
and errors; all 1,008 solution tests passed, including 69 Merchant tests. The key
tests verify the documented format/hash and compatibility/revocation of old keys.
