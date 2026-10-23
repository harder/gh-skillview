# October 2026 dependency refresh

This refresh combines the SDK selection and gh compatibility work with the
reviewed runtime dependency updates, based on main at `3c02bbb`.

| Component | Selected version |
| --- | --- |
| .NET SDK | 10.0.113, roll-forward disabled |
| Runtime, AOT compiler, linker | 10.0.13 |
| Markdig | 1.4.2 |
| Microsoft.Extensions runtime packages | 10.0.13 |
| System.IO.Abstractions and TestableIO wrappers | 22.3.0 |
| Testably.Abstractions.FileSystem.Interface | 10.4.0 |
| CodeQL Action | 4.39.1, commit `9b3254085494b55a0f7c4c2b3d2d52cc712ce370` |

Terminal.Gui 2.5.0, Terminal.Gui.Editor 2.5.7, YamlDotNet 18.1.0, Tomlyn 2.10.1,
and the test platform packages remain at their current stable versions.
Core explicitly overrides seven Terminal.Gui runtime dependency roots so the
serviced versions survive ordinary restore. All six lock files were regenerated
and locked restore passed. Their AOT/compiler versions remain exactly 10.0.13.

Reviewed [Markdig 1.4.0](https://github.com/xoofx/markdig/releases/tag/1.4.0),
[1.4.1](https://github.com/xoofx/markdig/releases/tag/1.4.1), and
[1.4.2](https://github.com/xoofx/markdig/releases/tag/1.4.2): the updates fix
Markdown parsing and add AOT compatibility annotations. Reviewed
[System.IO.Abstractions 22.3.0](https://github.com/TestableIO/System.IO.Abstractions/releases/tag/v22.3.0):
it advances the shared filesystem interface to 10.4.0. Microsoft.Extensions
updates stay within the serviced .NET 10 patch line. The
[CodeQL 4.39.1 release](https://github.com/github/codeql-action/releases/tag/v4.39.1)
updates its default analysis bundle to 2.27.3.

The final NuGet query reports no known vulnerable dependencies. Only the
test-only transitive Microsoft.ApplicationInsights 2.23.0 and
Microsoft.Bcl.AsyncInterfaces 6.0.0 have newer stable versions available. Leave
their major-version transitions with the owning test-platform packages rather
than adding unrelated overrides.

The [gh 2.103 review](gh-2.103.0-compatibility.md) and its complete 243-commit
ledger retain the initial session's evidence from the older checkout. The work
was then integrated onto current main; the combined branch uses the refreshed
dependencies and repeats the minimum/latest live gh contracts, standalone and
extension smokes, full tests, formatting, and automation checks. The PR's CI
provides cross-platform tests and all four native AOT targets before merge.

Local combined-branch results on Linux ARM64: locked restore and warning-free
build passed; full tests passed with 2,718 successful cases and 30 explicit
platform skips; both gh 2.97.0 and 2.103.0 opt-in contract lanes passed 19/19.
Standalone doctor/search/versioned preview, real extension `GH_PATH` selection,
formatting, dependency-monitor script tests, and the static site checks passed.
