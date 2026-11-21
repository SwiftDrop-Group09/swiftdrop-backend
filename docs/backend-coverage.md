# Backend test coverage

Backend CI collects coverage in the full Release solution test run and
publishes the `backend-verification-<tested SHA>` artifact. On a pull request
that SHA identifies the tested merge ref. The artifact contains TRX,
per-project Cobertura, and merged Cobertura, HTML and summary reports.
Reporting and artifact upload run after a failed test step when test
outputs are available. The test step's failure still fails CI.

The merged totals divide covered executable lines by coverable executable
lines and covered branches by coverable branches. ReportGenerator merges
the OpenCover input before computing percentages, so assemblies exercised
by multiple suites contribute a single denominator. The report shows
each workload and shared assembly alongside the combined result.

Coverage includes the hand-written SwiftDrop backend assemblies, including
endpoint, middleware, domain, infrastructure and migration implementation
code. Generated compiler output, files marked as generated, EF migration
designers and model snapshots are excluded because they reproduce framework
or schema metadata. The Gateway's composition-only entry point is excluded
because it wires the tested middleware and proxy. Test and harness assemblies
are outside the application denominator.

Compare combined in-scope line coverage with the team's 80% business logic
target. The broader backend denominator retains hand-written infrastructure
logic. Use the HTML report to prioritize gaps by uncovered line count and
business impact, starting with authorization and data integrity paths.
Foundation and Docker smoke tests validate deployment behavior independently.
