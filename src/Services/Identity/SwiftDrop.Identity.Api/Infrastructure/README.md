# Identity Infrastructure

Own database and external-system adapters. This service alone may access
`swiftdrop_identity`.

`PostgresUserRegistrationStore` writes a user, Customer role assignment, and
audit entry in one transaction. SQL migrations are embedded and applied
explicitly with `dotnet run --project src/Services/Identity/SwiftDrop.Identity.Api -- --migrate`
from the backend repository, or by the Docker Compose one-shot migration
service. Both host and Docker runners apply the migration before starting
Identity. Migrations use a separate `identity_migrator` login with schema DDL
rights; the API uses `identity_app` with table-specific DML grants and no DDL
rights. Future migrations must grant only the permissions needed on their new
tables. The app requires
`ConnectionStrings:Database`; migration requires
`ConnectionStrings:MigrationDatabase`; `/ready` checks Neon connectivity and
the current migration version.

Namespace: `SwiftDrop.Identity.Api.Infrastructure`.
These are folders within one project; dependency direction is a review
convention, not compiler-enforced project isolation.
