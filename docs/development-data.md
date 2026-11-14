# Operational development data

On 14 November 2026 the configured Identity, Delivery, Merchant and Notifications
databases received linked Sri Lankan business and workflow records. Existing
accounts, credentials, sessions, history, catalog rows and migrations were
preserved. No schema or constraint was changed for the import.

## Added records

| Area | Records added |
| --- | --- |
| Identity | 38 accounts with one role each and account-import audit entries |
| Merchant | 8 businesses: 4 Approved, 2 Pending, 1 Rejected, 1 Suspended |
| Delivery operations | 9 courier profiles, 225 zone memberships, 228 parcels, 202 assignments |
| Booking provenance | 228 price snapshots, 171 consumed Customer quotes and 171 completed booking idempotency records |
| Delivery history | 1,201 status transitions and corresponding published outbox records |
| Proof | 152 private signature PNG objects and matching proof metadata |
| Pricing | 75 inactive retained tariffs; the existing 25 zones and 75 active tariffs are unchanged |
| Merchant orders | 57 settled Created inbox records linked to the Delivery parcels |
| Merchant integrations | 8 revoked keys, 8 inactive subscriptions, 51 terminal webhook jobs, 55 attempts and 1 replay audit |
| Notifications | 1,201 consumed events, 150 completed notification jobs with suppressed attempts, 50 completed forwarding jobs |

Parcels span 15 September through 14 November 2026. Their current states are
152 Delivered, 28 Failed, 20 Cancelled, 6 Created, 6 Assigned, 6 PickedUp,
5 InTransit and 5 OutForDelivery. Addresses use the existing district catalog;
weights, parcel sizes and LKR charges agree with their retained tariff snapshots.
The records supply completed history for report comparisons and current work
for Customer, Courier and Dispatcher views. No UI labels or mocked totals were
introduced.

## Preservation and message safety

The import used parameterized transactions in each owning database, retaining
normal foreign keys, check constraints, immutable-record triggers and deferred
checks. Deterministic identifiers allow safe retries. A second committed run
affected zero rows in every database. This is a one-time development operation;
normal startup and schema migration do not reinsert the dataset.

All imported Delivery events were inserted with `published_at` already set.
Imported Merchant jobs are terminal and their subscriptions inactive. Imported
notification/forwarding jobs are Completed with no next attempt or lease;
provider attempts are Suppressed with `history_import_no_send`. Historical
integration records do not establish actual message delivery or provider calls.
Reserved `.test` endpoints and email domains are used. Existing integration
records and their configuration were left unchanged.

Inactive integration records also exist for unapproved imported businesses;
they confer no access, and normal approved-business checks still govern setup.

No external identity links, refresh sessions, migration-history rows or role
migration archives were fabricated. New passwords use the real Identity Argon2
hasher; credentials and the import tooling remain outside the repositories and
must not be committed or placed in documentation.

## Verification and limits

All four databases passed rollback validation before commit. Read-only checks
after commit verified preservation of every pre-existing row using row hashes,
single-role accounts, owning Customer/Courier/business relationships, snapshot
totals, history versions, assignment zones/capacity, proof bytes/digests and
non-runnable imported jobs.

The configured runtime roles successfully exercised the actual Delivery report,
parcel/detail/tracking and Dispatcher workload stores and loaded Merchant order
payloads through the native command materializer and fingerprint function.
The dashboard's selected 30-day report returned 94 Delivered, 12 Failed and
10 Cancelled; its preceding equal period returned 57, 16 and 9 respectively.
These counts describe this verification window, not permanent expected totals.

This does not establish end-to-end broker/provider acceptance. The Notifications
schema is present, but this checkout's worker still exposes only its host health
surface. Delivery writes proofs to PostgreSQL, while `IPrivateProofReader` is
currently registered to S3 in `Program.cs`; stored evidence was verified directly
and image download through that separate adapter is not claimed.
