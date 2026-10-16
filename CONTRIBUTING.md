# Contributing to SwiftDrop Backend

Work on a focused branch from the team's agreed base. Do not work directly on
main. Keep commits local unless explicitly asked to push; commits and PRs require
the contributor's normal review workflow.

## Development conventions

- Use the SDK pinned by global.json and the single SwiftDrop.sln solution.
- Follow .editorconfig: UTF-8, LF, four-space C#, two-space JSON/XML/YAML and
  file-scoped namespaces. Directory.Build.props centralizes net10.0, nullable,
  implicit usings, deterministic compilation and warnings as errors.
- Keep `SwiftDrop.<Capability>.Api.<Layer>` namespaces aligned with folders.
  Dependency direction is Presentation -> Application -> Domain; Infrastructure
  implements Application ports. Folder boundaries need review within each API.
- Gateway forwards requests and owns no domain behavior. REST, GraphQL, SSE and
  internal gRPC adapters belong to their documented service; future adapters
  must reuse that service's Application handlers and authorization policies.
- Each API and Worker owns its database and credentials. Communicate via
  versioned integration contracts or private APIs, never another service's tables.
- Contracts must remain dependency-free integration data/schema assets.
  Version breaking wire changes explicitly; never share domain entities or rules.
- Store credentials in per-project Development user secrets or environment
  variables. Do not commit local configuration, tokens, keys or connection secrets.
  The sanitized example documents future keys without enabling adapters.
- Keep liveness separate from dependency readiness. Do not add a successful
  placeholder route for a feature or mark unimplemented functionality complete.

The canonical architecture and contract baseline is linked from README. Treat it
as proposed until the team approves it; record approved decisions before changing
boundaries. Do not edit the documentation repository as part of backend work.

## Verification and review

Run from the repository root:

```sh
dotnet restore SwiftDrop.sln
dotnet build SwiftDrop.sln --no-restore --configuration Release
dotnet test SwiftDrop.sln --no-build --configuration Release
```

Then run `./scripts/verify-foundation.ps1` in PowerShell with local scaffold
ports free. It audits allowed project references and cycles, exercises real host
startup and forwarding, and cleans up its own processes.

Use tests that verify behavior and boundaries. Update the gateway route fixture
with a cited baseline when ownership changes. Do not accept empty passing tests.
Review the diff for secrets, unexpected references, added runtime dependencies,
and routes that bypass service ownership or expose internal endpoints.
Describe the concrete change, validation and remaining limitations in a PR.

## Commit scope and verification

Use focused commits that can be reviewed and verified independently. Typical
scopes for foundation changes are:

1. `chore: configure shared development standards` — props, SDK, editor and ignore rules.
2. `feat: initialize gateway routing and service health endpoints` — five hosts,
   route/listener settings, removal of template endpoints and fake worker loop.
3. `build: establish backend contracts and local configuration` — Contracts assets,
   references, API layer folders and sanitized example.
4. `test: verify backend foundation and routing boundaries` — test host references,
   foundation tests, gateway tests/solution entry, fixture and smoke script.
5. `ci: add backend build and test workflow` — build, tests and process verification.
6. `docs: document backend development setup` — README and contribution guide.

Stage related project-file hunks with their owning group and run the full
restore/build/test checkpoint before finalizing each commit. Keep the gateway
test project and its solution entry together. Commit groups are suggestions:
regroup dependent project-file and source changes so each actual commit builds
and tests independently.

## Next isolated milestone

Add container/dependency configuration and startup validation in a separate change:
private listeners, four isolated PostgreSQL databases/users, RabbitMQ credentials
and dependency-aware readiness. Keep business handlers, authentication and EF
migrations out until their own approved milestones. Do not expose protected
operations before authentication and ownership checks are ready.
