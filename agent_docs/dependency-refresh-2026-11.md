# November 2026 dependency and security refresh

This refresh starts from main `6b08afd8`. It does not include the unqualified
rank2/R0 experiments or unrelated unmerged UI/CI repairs. The October refresh and
older compatibility/performance receipts remain historical records, not results
for this candidate. Publication and cross-platform/native acceptance are separate
from the local checks described here.

## Selected versions

| Component | Before | Selected |
| --- | --- | --- |
| .NET SDK | 10.0.113 | **10.0.403**, roll-forward disabled |
| Runtime, AOT compiler and linker | 10.0.13 | **10.0.14** |
| Terminal.Gui | 2.5.0 | **2.5.1-develop.183**, explicit maintainer-approved preview |
| Terminal.Gui.Editor | 2.5.7 | **2.5.8-develop.6**, paired preview |
| Markdig | 1.4.2 | **1.6.0** |
| Microsoft.Extensions runtime packages | 10.0.13 | **10.0.14**, including required transitives |
| TextMateSharp and Grammars | 2.0.4 transitive | **2.0.5**, explicit servicing roots |
| Microsoft Testing Platform components | 2.5.1 transitive | **2.5.2**, explicit test-only servicing |
| Microsoft.Bcl.AsyncInterfaces | 6.0.0 test-only | **10.0.14**, explicit test-only servicing |
| gh security minimum / recorded current | 2.97.0 / 2.102.0 | **2.102.0 / 2.103.3** |
| setup-dotnet Action | 6.0.0 | **6.1.0** |
| CodeQL Action | 4.39.1 | **4.39.3** |
| action-gh-release | 3.0.3 | **3.0.4** |
| Copilot CLI, read-only dependency assessment job | 1.0.92 | **1.0.106** |

All six lock files are intentionally regenerated, then locked-restored, including
`net10.0` and linux-x64/osx-arm64/win-x64/win-arm64 compiler sections. Other direct
stable packages remain current: YamlDotNet 18.1.0, System.IO.Abstractions 22.3.0,
Microsoft.NET.Test.Sdk 18.11.0, xunit.v3 4.0.2 and its VS runner 4.0.1. The stable
Terminal.Gui/Editor releases are still 2.5.0/2.5.7. No .NET 11 or incidental
prerelease is adopted.

The .NET choice and official SDK hashes come from Microsoft's
[10.0 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
The exact user-approved GUI package identities are
[GUI develop.183](https://www.nuget.org/packages/Terminal.Gui/2.5.1-develop.183)
(source `84ea67cb4b43c3e711b54dfb46d3facce078f292`, published October 30) and
[Editor develop.6](https://www.nuget.org/packages/Terminal.Gui.Editor/2.5.8-develop.6)
(source `b33bf48e01c80654e6b695e782b0733fbeb29204`). The semantically greater
`2.6.0-develop.61` was published September 3 and is an older stream; it is not the
selected update. Editor's stable `>=2.5.0` dependency does not itself select the
GUI preview, so both properties stay explicit and exact. The dependency monitor
can read these preview properties but continues checking stable upstream releases,
including the eventual stable release of the same version, without a downgrade.

## Security and compatibility decisions

- [.NET 10.0.14's OOB security notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.14/10.0.14.md)
  address CVE-2026-111497 (Brotli decoder out-of-bounds write) and
  CVE-2026-114148 (Linux tarball ownership). Rebuilding the self-contained hosts is
  required; changing a developer's SDK does not patch an already shipped binary.
- [GHSA-qcwj-mr2r-2cx7](https://github.com/cli/cli/security/advisories/GHSA-qcwj-mr2r-2cx7)
  and [GHSA-39wj-f2f4-978v](https://github.com/cli/cli/security/advisories/GHSA-39wj-f2f4-978v)
  require gh 2.102.0. Keep SkillView's trusted-flags-before-`--` boundary even
  above that floor. A prerelease label does not prove a stable security fix.
  Runtime admission, docs, WinGet's current template and minimum contract lane
  use the new floor; historical schema fixtures and general old-version parser
  cases retain their original values.
- **One intentional latest-version exception:** test-only
  Microsoft.ApplicationInsights remains **2.23.0**, not 3.2.0. Published MTP 2.5.2
  still declares that dependency, and its pinned
  [consumer](https://github.com/microsoft/testfx/blob/933377f07ac4bded77e820b0beef7d27e805cd89/src/Platform/Microsoft.Testing.Extensions.Telemetry/AppInsightTelemetryClient.cs)
  calls `TrackEvent(eventName, properties, metrics)`. The 3.x
  [breaking-change contract](https://github.com/microsoft/ApplicationInsights-dotnet/blob/c896b342f3c52766f148f9b3b1b3411e27c05da3/BreakingChanges.md)
  removes that overload and replaces the telemetry architecture with OpenTelemetry.
  Blindly overriding the major dependency can break the runner even if restore
  accepts its lower-bound version range. Revisit with the owning test-platform
  release; do not claim the entire graph is at every package's highest version.
- Action tags were resolved to full commit SHAs from their official repositories;
  comments name exact releases. CodeQL's `releases/latest` can be a bundle tag:
  `codeql-bundle-v2.27.4` is **not** the Action version. The selected Action is
  [v4.39.3](https://github.com/github/codeql-action/releases/tag/v4.39.3), commit
  `33d7b85da1a04171e211befe2c739303b9a4c066`. All other pinned Actions were checked,
  including ones already current. Permissions, deadlines, coverage and default
  security checks remain unchanged.

## Useful features actually included

The exact published GUI pin contains the stable-to-package 183-commit delta,
not every commit on today's develop branch. Automatic benefits include Markdown
partial-content redraw, table-width caching and Markdig/AOT fixes; UTF-8 batched
output and failed-frame recovery; callbacks outside the timer queue lock; Windows
non-BMP input and TreeView grapheme fixes; and focused ListView/menu/input fixes.
See the [exact package source comparison](https://github.com/tui-cs/Terminal.Gui/compare/v2.5.0...84ea67cb4b43c3e711b54dfb46d3facce078f292).
New local regressions exercise actual package rendering, full glyph/attribute-grid
parity after Markdown viewport invalidation/content replacement, Unicode, code
copy text, and Editor typed/polymorphic Text/document/read-only selection behavior.
Linux tests do not qualify Windows input, physical terminal emission or speed.

The five later, unpublished develop commits (submenu/title automatic hotkeys and
Arrange-mode exit changes among them) are not advertised as included. No backport
or failed rank2 optimization is imported. Existing app-owned dispatch, generation,
modal-start, cancellation and drain-before-dispose guards remain. New framework
session-aware `InvokeAsync` and inline print-above could support a future contained
change, but are not adopted as untested ownership or UI-mode cleanup here.

[gh 2.103.3](https://github.com/cli/cli/releases/tag/v2.103.3) improves expired-token
hints under `--no-refresh` after HTTP 401; 403/503/transport failures are not expiry
proof. Fake parser tests cover expired/refresh-expired failures on either stream,
including another login marker, without reading credentials or invoking live auth.
Production still delegates ordinary refresh to gh. The 2.103.2 file-attachment
installation-token change is not a `gh skill install` quota fix. The skill surface,
48-agent catalog and placements have no source changes in the 2.103.1..2.103.3
delta. Official checksum-verified minimum/current tools remain separate, and pinned
contract-lane downloads now reuse the bounded checksum verifier.

The CLI version report uses the loaded GUI assembly's informational version so a
preview is not mislabeled as the stable-looking assembly version `2.5.1.0`.

## Frozen producers and remaining qualification

Current products use SDK403/runtime14. The five frozen source commits, their six
locks, SDK402/113 selections, compiler13 pins and publish recipes are unchanged.
The current CI helper binds expected source SDK/global/lock hashes separately
from the actual SDK selected in that source CWD, using schema-3 cache/transfer
receipts. `setup-dotnet` installs the exact old versions beside the current SDK;
no current compiler is silently substituted. Old schema-1/2 receipts are refused.
See [CI runtime optimization](ci-runtime-optimization.md).

Historical compiler13 fails default audit. The owner has now approved one exact
historical CI-only exception; it does not disable audit or rewrite immutable
source. The explicit five-source policy, default-admission and versioned-props
bindings are described in the CI runtime guide. All supported historical schemas, twelve native
families, both hosts, four RIDs, AOT warnings, source/retention/native-identity and
security gates remain required before integration. No local Linux check is called
complete Windows/macOS/native/CI coverage. No old failed run is relabeled green.

There is no application npm/pip dependency graph: site code and Python tools use
browser/Node/stdlib APIs. The site retains its optional, unversioned Google Fonts
CSS service with local font fallbacks; there is no fabricated font package pin or
external-service security/availability claim. Copilot is an exact pinned ancillary
CI tool, not a product dependency; its npm graph is separately audited as metadata
without launching an AI client. Compiler, Git, Python, Node and OS packages used
locally are recorded build-environment facts, not a claim to upgrade the host OS.

Local final results and unresolved gates are recorded with exact source/package
bindings in the refresh evidence report; source review and exact-head CI are still
required before publication.


## Local verification and explicit remaining blocker

For the final C# candidate, SDK 10.0.403/runtime 10.0.14 produced a zero-warning
six-project Release build and full-solution style verification (555 workspace
files, zero changes). The unfiltered suite reported 3,046 cases: 3,014 succeeded,
zero failed and 32 existing platform/privilege skips. Four new actual-package
Markdown/Editor rendering cases passed, including normal partial repaint and
full glyph/attribute-grid comparison at two sizes. Native Python (22), CI Python
(14), Node (25), default Actionlint with ShellCheck, site checks and the ancillary
npm metadata audit passed. Current NuGet vulnerability inspection reported no
known vulnerable packages; outdated inspection reported only the documented
ApplicationInsights major-version exception.

Both final-source Linux AOT hosts published sequentially with the strict IL
warning gates and no emitted warnings. Their isolated CLI/PTY smokes observed
Unicode installed detail rendering, normal quit, child exit zero and completed
output drainage. Two earlier private harness failures remain failed evidence;
they are not product failures or accepted smoke results. These checks are not a
performance measurement, physical-emission proof or native Windows/macOS test.

Two graph-faithful default audited historical App restores, using their actual
source-selected SDKs 10.0.402 and 10.0.113, both failed NU1903 for
Microsoft.DotNet.ILCompiler 10.0.13 and
[GHSA-p3jp-9v83-76w9](https://github.com/advisories/GHSA-p3jp-9v83-76w9): an App
warning and a Core warning-as-error. Two earlier diagnostic commands incorrectly
specified restore `-r`, which conflicted with the frozen four-RID lock graph and
failed NU1004; those failures are retained separately. Runtime/download-only pack
audit coverage cannot be inferred from absent messages. Those pre-approval
failures remain immutable. The owner subsequently approved the exact five-fixture
compiler13/high/advisory exception. The helper now requires the known-only default
failure before applying its one-item external props to a historical build or
accepting cached tools. Two new representative paths (SDK402/legacy and
SDK113/reviewed-merge) completed that admission and each performed one suppressed
locked restore with exit0; App AND Core assets positively serialized the item,
while audit stayed enabled/low/all. No historical AOT/runtime/native proof ran.
The first new legacy parser rejected a normal first-use stderr banner after a
complete stdout footer; separate-stream parsing was corrected and its retained
completed default output reused, without repeating the restore. These are local
restore/identity checks, not historical CI qualification. Exact-head CI, all native
families/platforms and full independent source/security review remain pending.


The exception implementation changes the current helper/tests, focused guides,
owner disposition JSON and named failure-receipt artifact selection; product
C#/packages are byte-identical to the earlier locally checked candidate. No
unchanged full managed suite, AOT publish or PTY was repeated for this tooling-only
delta. The additional historical publish check rejects any NuGet diagnostic or
nonzero exit; compiler/linker warnings retain the original recipe and strict IL
warning rules. Refused audit/publish streams are retained by the always-run
receipts artifact without recursively capturing private toolstate.
