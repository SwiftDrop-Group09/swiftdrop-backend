# Single human role

Every user has exactly one HumanRole: Customer, Courier, Merchant, Dispatcher or
Admin. Identity stores assignments as PostgreSQL public.user_role enum values;
user_roles.user_id is unique and deferred triggers require one assignment at
transaction commit, including registration and role replacement. No arbitrary
varchar role can be assigned. Inactive accounts retain their role. The shared
HumanRole enum provides strict named-value parsing (numeric and case aliases
are rejected).

For v1 compatibility Profile.roles, RolesInput.roles and JWT roles remain arrays
with exactly one enum name. Identity rejects invalid assignment cardinality and
issuance. Identity/Delivery token validation rejects zero/multiple roles;
Merchant human JWTs require one named role. Service API-key scopes are unchanged.
The last-active-Admin guard and transactional audit remain authoritative. The
Admin provisioner creates Admin only; normal registration creates Customer only.

## Forward migration 005_single_user_role

Run the owning Identity migration task with schema-owner credentials before
starting the updated service. Apply once through IdentitySchemaMigrator; repeated
runs skip recorded versions. Do not edit deployed migrations 001?004 or remove
migration-history rows.

The approved legacy policy retains Admin first, then Dispatcher, Merchant,
Courier and Customer. Accounts with no role become Customer. All old assignments
(including roleless accounts) are first copied to owner-only
user_role_migration_archive. The migration only removes redundant assignment
rows; accounts, password hashes, external identities and business data remain.
The complete migration is transactional under the existing advisory lock. Unknown
legacy role values fail conversion and roll the entire migration back rather
than silently discarding them. Audit snapshots retain historical arrays.

Review/back up valuable shared data before rollout. This implementation has not
migrated shared Neon databases. Restart every service and client after deployment;
previous ambiguous human tokens are rejected and must refresh/re-authenticate.
The precedence is migration policy, not role inheritance: Admin does not gain
Customer/Courier operational rights.

Focused disposable-PostgreSQL tests cover all precedence choices, roleless users,
credential preservation, archive retention, repeat runs and database rejection of
second, missing or unknown roles. Runtime role-change and last-Admin concurrency
tests continue to apply.

Admin account creation additionally accepts an optional role enum name, defaulting
to Customer. It is checked before password hashing and persisted atomically with
the new account and audit event. This does not change public registration or
allow applicants to self-assign privileges.
