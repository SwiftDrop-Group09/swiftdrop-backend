# Delivery Phase 2: quote verification and request-encoding fix

## Reinspection and scope

Continued on 25 October 2026 from the tested Phase 1 checkpoint `dc4cf46`.
The clean backend tree already includes the quote and confirmation work merged
in `32604d4`; this execution does not recreate those implementations. Branch
`feat/delivery-quotes-verification` starts from `dc4cf46`. No AGENTS.md was found.

Other repositories remain clean and unchanged: docs main `d97fff4`, infra main
`075a8b4`, frontend main `c062b11`, mobile main `e9778c1`. Relevant current source,
tests, contracts, REST/data/security documentation and the historical Phase 2
report were reinspected. Historical reports describe their own executions, not
the current baseline or results below. The architecture remains Proposed except
the narrower recorded Neon, development-secret and tariff-zone confirmations.

This execution is Phase 2 / FR-C05 only. Existing booking confirmation is
preserved; no new Phase 3 work, client screen, Identity implementation, Merchant
adapter or relay is introduced. The Phase 0/1 discovery and unresolved deployment
issues remain documented in [the foundation assessment](delivery-foundation-verification.md).

## Existing contracts reused

`POST /api/v1/quotes`, operation EstimateFee, requires an authenticated Customer.
Input is pickupAddress/deliveryAddress (street, city, postalCode, zoneId),
packageDetails (numeric weightKg > 0 and <= 30; Small/Medium/Large), and recipient
(name, phoneNumber). Ownership comes from the verified principal. Strict JSON
rejects duplicate/unknown fields and numeric strings; validation precedes storage.

The application validates both active zones and selects the unique active
tariff for **deliveryAddress.zoneId and package size**. Multiple/no active tiers
fail safely rather than selecting an undocumented latest version. The price is
baseFee + perKgFee * max(0, weightKg - 1), exact decimal-derived arithmetic, LKR,
half-up to two decimals. Test prices remain fictional fixtures; no operational
tariffs or postal-zone configuration are invented.

HTTP 200 returns exactly quoteId, total, currency, pricingVersion, expiresAt and
fingerprint. The database retains owner, normalized complete input, tariff
identity/version/currency, fingerprint and ten-minute expiration. Estimation
inserts only a quote, without parcel, snapshot, history, idempotency or outbox.
No tracking number is generated. Existing domain/application/store/DI and the
Phase 1 schema/migration are reused.

Canonical sources: docs `api/rest-api.md:28`, `:85`, `:106`;
`architecture/data-architecture.md:22`, `:24`, `:26`;
`security/authorization.md:9`; `testing/test-cases.md:13`.

## Assessment, implementation and files

The pre-change quote suite passed 192/192. Review found no numeric-pricing or
canonical-fingerprint defect, but identified an unhandled request-encoding
failure and missing real-database tariff-selection combinations. The plan was
to reproduce that transport failure, fix only its parser handling, add targeted
PostgreSQL tests, and run regression/build/format/migration/startup checks.

All changes are in **swiftdrop-backend**:

- `src/Services/Delivery/SwiftDrop.Delivery.Api/Presentation/QuoteEndpoints.cs`:
  catch the ASP.NET JSON reader's unsupported-charset exception shape
  (InvalidOperationException with an ArgumentException cause) and return the
  existing safe 400 validation ProblemDetails. The catch is limited to request
  parsing; unrelated application/storage errors are not reclassified. No parser
  exception text, charset value or private request body is returned.
- `tests/SwiftDrop.Delivery.Tests/Quotes/QuoteRequestEncodingTests.cs`: valid
  authenticated JSON with `charset=unknown-swiftdrop-charset` and no configured
  database verifies 400, validation code/body field/correlation, and no private
  data or parser exception details. Before the fix, the executed test failed:
  expected BadRequest, actual InternalServerError. The stack trace confirmed
  the framework's encoding wrapper.
- `tests/SwiftDrop.Delivery.Tests/Quotes/QuoteApiTests.cs`: six PostgreSQL/HTTP
  cases cover all three sizes with identical or distinct pickup/delivery zones.
  Competing size tariffs and pickup-zone tariffs demonstrate correct selection;
  each case checks persisted tariff/version/owner/input/fingerprint/total and
  that only one quote was added across all six booking-related table counts.
- `docs/delivery-quotes-verification.md`: this assessment and observed handoff.

No DTO, route, response-field, database-model, migration, package, project
reference, Gateway/root configuration or other repository changes are needed.
Existing `20261023142857_InitialBookingPersistence` remains the only migration.
No shared database migration or reset is performed.

## Observed validation

Commands run from the backend root using SDK 10.0.301 at
`C:\Users\Chamza\AppData\Local\Codex\tools\swiftdrop-dotnet-10.0.301`, prepended
to process PATH and DOTNET_ROOT. Test logging overrides disable only Windows
EventLog. PostgreSQL integration uses the existing private temporary-cluster
fixture, not the running service on 5432 or shared Neon.

| Command/check | Observed result |
| --- | --- |
| `dotnet restore SwiftDrop.sln` | Passed; all 11 projects up to date. Package audit was not disabled. |
| Pre-change Release build and quote regression | Passed; zero build warnings/errors and 192/192 quote tests, zero failures/skips. |
| New unsupported-charset test before the fix | Failed as expected: HTTP 500 instead of 400. Framework stack trace confirmed InvalidOperationException with ArgumentException cause. |
| `dotnet test tests/SwiftDrop.Delivery.Tests --no-restore --configuration Release --filter 'FullyQualifiedName~UnsupportedCharsetReturnsSafeValidationProblemBeforeDatabaseAccess\|FullyQualifiedName~QuoteSelectsTheRequestedDeliveryZoneAndSizeWithNoBookingSideEffects' --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'` | After the fix: passed 7/7, zero failures/skips, 10 seconds. |
| `dotnet build SwiftDrop.sln --no-restore --configuration Release` | Final solution build passed; zero warnings and zero errors. |
| `dotnet test SwiftDrop.sln --no-build --no-restore --configuration Release --environment Logging__EventLog__LogLevel__Default=None --logger 'console;verbosity=minimal'` | Passed **463/463**, zero failures/skips: Delivery 372, Gateway 68, Identity 12, Merchant 6, Notification 5. Delivery completed in 1 minute 18 seconds. |
| `dotnet format SwiftDrop.sln --no-restore --include src/Services/Delivery/SwiftDrop.Delivery.Api/Presentation/QuoteEndpoints.cs tests/SwiftDrop.Delivery.Tests/Quotes/QuoteApiTests.cs tests/SwiftDrop.Delivery.Tests/Quotes/QuoteRequestEncodingTests.cs --verify-no-changes --verbosity minimal` | Passed. |
| `dotnet ef migrations has-pending-model-changes --project src/Services/Delivery/SwiftDrop.Delivery.Api --configuration Release --no-build` | Passed; no model changes since the existing migration. |
| `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-foundation.ps1` | Passed: 11-project boundary/cycle audit, five hosts, 15 missing-feature/internal/readiness probes, offline Identity readiness, real Gateway quote/booking POST rejection despite caller-supplied identity, collection GET and private Delivery listener. Script stopped all owned hosts. Machine policy unchanged. |
| SHA256 comparison: backend delivery.proto and StatusChanged v1 schema versus docs | Both match exactly. |
| `git diff --check`, other-repository status and PostgreSQL fixture cleanup | Passed; other repositories clean, temporary PostgreSQL cluster directory removed. |
| `docker info --format '{{.ServerVersion}}'` | Blocked: Docker Desktop Linux engine named pipe absent. No container build/start claimed. |
| Live Neon, hosted CI, real Identity token verification, React/Flutter | Not executed; no deployed secrets/login or client changes were introduced in this checkpoint. |

The seven added cases and all prior tests are implemented and tested. Production
readiness is not implied by an isolated test principal or an outbox row.

## Dependencies and stopping point

Real Identity issuer/audience/RS256/JWKS validation remains outstanding. Normal
deployed quote/booking requests return 401 through the existing closed handler;
successful HTTP tests use principals registered only in test hosts. These tests
do not establish a live login-to-quote flow. Existing invalid-identity/role and
ownership tests remain part of the regression suite.

The documented runtime/migration credential conflict, absent live Neon
permissions/connectivity evidence, unavailable Docker daemon and unexecuted
hosted CI remain deployment limitations. Postal-to-zone mapping has no defined
boundaries configuration; explicit zone eligibility is validated. No tariff
activation ordering rule is assumed. Docs fixtures remain Proposed; the docs
repository was not edited to hide those limitations.

React/Flutter authentication/networking and Customer booking screens remain
later client work. Merchant CreateBooking/GetBookingByExternalOrder and broker
relay/notification delivery remain separate integrations. Quote estimation does
not produce a delivery event, and no broker-delivery result is claimed.

The focused logical commit/PR is
`fix(delivery): reject unsupported quote request charsets`, including the
confirmed parser fix, seven regression cases and this report. Commit locally as
requested, without push/merge/PR creation. Stop at Phase 2 before new Phase 3
implementation.
