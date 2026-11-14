# Activity audit implementation

The Admin audit workspace reads service-owned records. Identity, Delivery and
Merchant each retain their own database and expose an Admin-only, private/no-store
read endpoint. `SwiftDrop.Auditing` is shared infrastructure; APIs do not reference
each other's assemblies or query each other's databases.

## Recording and privacy

Mapped `/api` and `/internal` requests are recorded after authentication and around
authorization/endpoint execution, including reads, mutations and denied attempts.
Health, readiness, static assets, unknown routes and OPTIONS are excluded. This is
server activity auditing, not mouse/scroll/theme telemetry. The current Worker has
no business consumer yet; future consumers must provide durable audit records.
Existing webhook attempts appear as System activity; no browser IP is invented
for background jobs.

Records contain actor UUID, roles at request time, actor type, service, named
action, method, route template, UUID resource reference, connection IP/source,
status, UTC start time, duration and an opaque correlation UUID. Bodies, response
bytes, authorization headers, cookies, API keys, query values, addresses, recipient
details and private delivery notes are not copied. Successful login/refresh and
Google login are attributed from the verified Identity result; registration uses
the committed new account ID. Invalid credentials remain anonymous.

An append-only Started event is written before execution and a Completed event
afterward. Reads select the latest phase for each operation. If audit storage is
unavailable, authenticated requests and authentication operations fail with 503
before execution. A completion-write failure leaves a durable Started record and
emits a critical diagnostic without payload/credential data. Such a record means
the completion is unknown, not that the business action failed. Anonymous denied
requests retain their original challenge if audit storage is unavailable. SSE
records remain started while the stream is open and complete after disconnect.

Historical Identity security, Delivery history/configuration, Merchant replay and
webhook-attempt records are projected alongside request activity. They remain
distinct record kinds; a request and its domain event are not collapsed into one
fictional event. Missing historical roles/IPs remain unavailable. Operational
table retention also governs those historical projections. New activity rows
reject UPDATE/DELETE, and rollback refuses to discard retained activity.

## IP trust

The default is `HttpContext.Connection.RemoteIpAddress`. Existing Identity trusted
proxy middleware may already have normalized it. Other services can set explicit
`Audit:TrustedProxies` addresses: only when the connecting peer matches that list
will the rightmost forwarded address be accepted. This assumes a single trusted
gateway that appends the peer address. Never trust the first caller-supplied
X-Forwarded-For value or configure arbitrary internet clients as proxies.

Without trusted ingress configuration, the recorded address can be the gateway
peer, not the browser's original public IP. Multi-hop ingress needs an explicitly
reviewed forwarding configuration before claiming end-user IP attribution.

## Deployment prerequisite

If local sign-in fails before showing Google's control, check
`GET /api/v1/auth/csrf` and Identity `/ready`. A 503 `audit_unavailable` together
with pending migrations means the service cannot durably record authentication;
CSRF tokens and Google nonces are deliberately not issued. Apply Identity's
pending migration using the explicit migration task below. Do not disable the
recorder or relax origin/cookie checks.

Audit failures return a private/no-store 503 with an opaque `correlationId` also
present in `X-Correlation-ID`. Operator logs classify missing schema, insufficient
permissions, connectivity, timeouts and invalid configuration with a SQLSTATE
when available and a recovery hint. They omit exception details, SQL parameters,
connection strings, request bodies and credentials. The public response does not
reveal database schema or privilege information.

Apply migrations before deploying/restarting the new API binaries:

- Identity: `004_actor_activity_audit` through the existing `--migrate` task and
  its separate migration connection. The embedded version ledger now expects 4.
- Delivery/Merchant: `20261113010000_ActorActivityAudit` through their existing
  EF/`--migrate` tasks using migration-capable credentials, not elevated runtime
  roles. Each migration grants only its own app role SELECT/INSERT on the table.
- Keep `Audit:Enabled=true` (the default) in the deployed application. Disabling
  recording is explicitly reported by the read API/UI; do not use it to bypass
  required migration/permissions during acceptance.
- Configure trusted proxies only from the actual deployment topology. Add these
  audit routes to any independently maintained gateway/ingress route allowlist.

No shared Neon schema was migrated by this implementation task. Database rollout
and real proxy IP verification remain deployment steps. Audit schema SQL respects
the configured schema for EF-owned services and isolated PostgreSQL tests.

## Verification

Dedicated tests explicitly enable auditing and verify server-attributed actors,
trusted/untrusted forwarding, no body/query leakage, append-only retention,
Admin-only reads, malformed filters and safe literal search. Historical source
projections run in disposable PostgreSQL schemas. Existing business tests disable
the cross-cutting recorder through `test.runsettings`; the test-only client harness
also disables it because its private store is injected rather than configured as
a production database. This does not disable existing transactional domain audits.

### Exclusion filters

The activity read endpoints accept `notActorId`, `notResourceId`,
`notCorrelationId`, `notIp`, `notRole`, `notAction`, `notMethod`, `notOutcome`,
`notKind` and `notActorType` with the corresponding positive filter's validation.
Rules combine with AND and apply before totals and cursor pagination. Nullable
IDs/IPs use IS DISTINCT FROM, so missing metadata remains included unless an
explicit positive filter selects it. `notAction` negates case-insensitive
substring matching. Role exclusion keeps rows whose recorded role array lacks
the selected role. All values are SQL parameters; no schema migration is needed.
