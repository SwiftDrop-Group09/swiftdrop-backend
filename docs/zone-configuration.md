# Zone configuration query

GET /api/v1/zones remains Admin-only. It accepts page (>=1), pageSize (1-100),
search (<=200 characters), city/notCity (<=200 each) and status (active/inactive).
Unknown/repeated query keys, malformed pagination and invalid status fail 400.
Text search is a literal case-insensitive substring across name, city and postal
codes. City comparisons are case-insensitive exact matches. Criteria combine
with AND. Values are parameterized; wildcard and SQL-looking input remain literal.

PostgreSQL applies criteria before stable name/ID ordering, count and pagination.
The read transaction uses RepeatableRead so rows, counts and city metadata share
one snapshot. ZonePage adds cities: distinct nonblank cities across all zones,
not merely the current page or filter. xmin-based version tokens remain intact.
No database migration is needed. Existing write transactions, overlap protection,
Admin authorization and configuration audit behavior remain authoritative.

The web sheet passes expectedVersion on edits, keeps failed drafts and separates
read retries from explicit mutation attempts. This work does not change zone
availability semantics, pricing calculation, assignment or ownership rules.
