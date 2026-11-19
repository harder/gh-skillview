# November 19 dependency and Copilot review

This follow-up starts from the simpler-UX merge `4f869e0`. The original
[November refresh](dependency-refresh-2026-11.md) remains historical evidence.

## Dependency choices

| Component | Selected | Reason |
| --- | --- | --- |
| .NET SDK / runtime and AOT | 10.0.404 / 10.0.15 | Current .NET 10 security servicing release; retain `net10.0` and disabled SDK roll-forward. |
| Microsoft.Extensions runtime roots / BCL async interfaces | 10.0.15 | Keep the supported servicing family consistent instead of partially adopting 11.0.0. |
| Microsoft.NET.Test.Sdk / xunit.v3 | 18.12.0 / 4.0.3 | PR #149 passed all 32 published checks, including the four native/AOT aggregates, before merge. |
| Microsoft Testing Platform roots | 2.5.3 | Update MSBuild, telemetry and TRX abstractions together. |
| Microsoft.ApplicationInsights | **[2.23.0]**, test-only | Exact constraint; the owning telemetry implementation still calls the removed three-argument `TrackEvent` overload. |
| System.IO.Abstractions | 23.0.0 | Supports .NET 10; complete lock-graph, test and AOT verification required. |
| Terminal.Gui / Editor | Existing exact approved pair | Current stable releases remain 2.5.0 / 2.5.7; do not replace the approved preview pair with an older stable stream. |

Microsoft's [10.0.15 release notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.15/10.0.15.md)
and [servicing metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
identify SDK 10.0.404. Its official archive is SHA-512 verified before use. Changing
SDK deliberately regenerates all six lock files, including all four native RIDs;
frozen historical producers keep their own SDKs and locks. The current privileged
registry-proof execution image moves to 10.0.404 independently of those inputs.

Initial hosted verification rejected compiler/linker hashes (NU1403) and missing
compiler runtime entries (NU1102). The local shared cache had supplied distro
packages from `/usr/lib/dotnet/library-packs` at the same version. Regenerating
with the verified SDK, a private package directory and nuget.org restores the
official hashes and all four RID graphs. Preserve the original failed runs;
do not bypass hash validation or use the warm-cache result as clean-feed proof.

The test SDK release notes distinguish the VSTest CLI artifact-processing security
fix from `Microsoft.NET.Test.Sdk`: updating only the package does not patch the CLI
shipped in an older SDK. The SDK servicing update is therefore included. New
experimental test-ID options remain disabled; ordinary runner fixes need no new
application feature or test-discovery policy.

### Application Insights compatibility

The published `Microsoft.Testing.Extensions.Telemetry` 2.5.3 package declares
Application Insights 2.23.0. Its exact
[consumer source](https://github.com/microsoft/testfx/blob/0b1d8b5f7b2186835e2cf26292f3478f7cca087e/src/Platform/Microsoft.Testing.Extensions.Telemetry/AppInsightTelemetryClient.cs)
still forwards `TrackEvent(eventName, properties, metrics)`. The upstream
[3.x breaking changes](https://github.com/microsoft/ApplicationInsights-dotnet/blob/c896b342f3c52766f148f9b3b1b3411e27c05da3/BreakingChanges.md)
remove that overload. Both test projects now carry an exact `[2.23.0]` constraint,
and Dependabot excludes 3.x while this consumer remains in use. This library is
not a product telemetry dependency. Revisit the constraint with an owning test
platform that actually supports the new API, rather than forcing the transitive
major version.

### Open PR dispositions

PR #149 is merged. The five partial .NET 11-family PRs (#150, #152–#155) are
replaced by consistent .NET 10 servicing. The three separate testing-platform
patch PRs (#156–#158) are consolidated so the roots and lock files stay aligned.
PR #159's recorded Linux restore failure is NU1004: only the Core lock changed,
leaving consuming project locks inconsistent. That failure alone does not prove
an API incompatibility; the complete 23.0.0 graph is evaluated in this follow-up.

The later duplicate alerts (#162–#169) are also disposed: #162/#164 defer the
same .NET 11 BCL major; the remaining six testing-platform patches are included
in the coordinated 2.5.3 update. The [GH CLI review](gh-2.105.0-compatibility.md)
records the dependency issues and every intervening upstream commit.

Future test-platform updates are grouped with the test SDK/xUnit. No experimental
APIs, new UI framework stream, .NET major migration, release tag, or relaxation of
native verification gates is part of this maintenance work.

## Copilot findings on PR #161

The review arrived after merge. The five small corrections retain the approved
layout: bound exceptional compact consumer output using the existing placement
renderer; escape literal tildes; retain collision-safe copy labels; put active
filter/sort state before routine coverage text at 80 columns; and preserve
update-check startup errors in the Installed footer. Tests cover literal paths
with the advanced Markdown pipeline, oversized/many consumers, exact copy
addresses and actually rendered minimum-width feedback.
