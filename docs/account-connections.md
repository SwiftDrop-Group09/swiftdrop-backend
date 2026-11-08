# Profile sign-in methods and password connection

The 9 November 2026 profile request extends Identity with two self-only operations.
It does not alter the existing Profile/TokenPair projections or other services.

| Method and route | Authorization | Result |
| --- | --- | --- |
| `GET /api/v1/auth/methods` | Authenticated active user; actor from JWT `sub` | `{hasPassword: boolean, googleLinked: boolean}` |
| `POST /api/v1/auth/password` | Authenticated active Google-only user plus fresh nonce-bound Google ID token; browser CSRF/origin checks | `{hasPassword: true, googleLinked: true}` |

The POST accepts exactly `{idToken, password}`. Password is 12-128 characters and
is hashed with the existing Argon2id implementation. Browser obtains a fresh
challenge from `/api/v1/auth/csrf`, then uses Google's SDK with that nonce. This
operation always requires that challenge, even for non-browser callers. The same
configured Google verifier validates signature, issuer, audience, expiry, verified
email and nonce. The store matches the verified **subject** to an existing Google
link owned by the authenticated user. A matching email alone is insufficient.

A transaction locks the Google subject and user, checks activation and absence of
a password again, then commits the hash and `PasswordAdded` audit together. Audit
failure rolls back the credential. Concurrent additions have one winner. This
operation cannot overwrite an existing password (`409 password_already_set`),
create another account, change email/roles, transfer a provider, or reset a
password. Existing Google access and sessions remain intact. Both new responses
are `Cache-Control: no-store`; no credential/hash/subject is returned or audited.

Connecting Google to a password account retains the existing
`POST /api/v1/auth/google/link` contract: current local password plus Google token,
same verified email, no implicit account merging, and atomic provider ownership.
The frontend reads method status from the server rather than inferring it from
the last login route or browser storage. Method status describes the account's
available credentials, not the authentication method used by a particular session.

No database migration or new secret is needed: the existing nullable
`users.password_hash`, `external_identities` and audit tables support this change.
Restart/rebuild Identity before using the corresponding frontend. Gateway's
existing `/api/v1/auth/**` route already forwards these operations. Canonical
documentation remains owned by the documentation repository; this records the
explicitly requested extension to its earlier baseline.

Verification uses the real disposable Identity PostgreSQL fixture: credential
states, self-only metadata, both login methods after addition, CSRF/origin/JWT
rejection, invalid/expired/mismatched nonce, foreign Google subject, disabled
user, input validation, overwrite prevention, concurrency, and audit rollback.

On 9 November 2026, restore and Release build passed with zero warnings/errors.
All 945 solution tests passed, including 369 Identity tests and the 19 new
connection cases. The foundation check passed the 12-project dependency/cycle
audit, then stopped at its listener preflight because development ports were
already occupied. It did not launch hosts or alter the running development stack.
