# GitHub CLI 2.103.1 compatibility review

Reviewed October 23, 2026 for [issue #89](https://github.com/harder/gh-skillview/issues/89).
[gh 2.103.1](https://github.com/cli/cli/releases/tag/v2.103.1) was published that day.
SkillView on merged main `3e6b69e27b3663b2748d9ca9e1c919b46fdc895e` is compatible
with this patch in the observed checks. Keep the minimum at **gh 2.97.0**.
No application adapter, agent catalog, placement, runtime or SDK change is needed.
The [2.103.0 review](gh-2.103.0-compatibility.md) remains the authentication and
extension baseline.

## Complete upstream commit review

The [2.103.0 → 2.103.1 comparison](https://github.com/cli/cli/compare/v2.103.0...v2.103.1)
contains seven commits and seven changed files. Reviewed every commit's files and
patches; each merge carries the same edited lines as its reviewed feature commit.

| Commit | Change and SkillView impact |
| --- | --- |
| [c61327f](https://github.com/cli/cli/commit/c61327f400134e11c84303613864e016542d86d2) | Skill-install review output and tests: keep the pre-install disclaimer, remove the repeated post-install warning, retain revision-pinned preview guidance. Stdout, flags, writes and tracking are unchanged. SkillView benefits automatically. |
| [5056c0a](https://github.com/cli/cli/commit/5056c0a8ef34512be1d5b4120d4e4b30412ada8b) | Merge of the install-output change, #14784; no additional edits. |
| [68987c3](https://github.com/cli/cli/commit/68987c3f9ca1b31c4837da4569eeebc43bded3d8) | Refresh the indirect Go `digitorus/pkcs7` revision and module checksums. This is inside gh; SkillView has no corresponding managed dependency to update. |
| [ed84a97](https://github.com/cli/cli/commit/ed84a97a559956853e4464fee3c0ff4262a0d6ac) | Merge of the Go dependency update, #14791; no additional edits. |
| [e87ed4c](https://github.com/cli/cli/commit/e87ed4c751918d89c87b041913973770a770dc5c) | Document Debian extrepo installation. No executable or SkillView change. |
| [da42d39](https://github.com/cli/cli/commit/da42d395407da8d2f93beb15e1fb973cd6477626) | Add a conditional interactive `auth login` stderr hint about `--expiring`, with tests. SkillView delegates status/API authentication to gh and does not invoke login. Existing token-refresh precautions still apply. |
| [64d1752](https://github.com/cli/cli/commit/64d175234b85aa7765c1f4cda058bd6353adb78f) | Merge of the login-hint change, #14798; final tag commit, no additional edits. |

The install change does not remove the pre-install warning or waive SkillView's
source review and retention approvals. The upstream tests cover local/remote and
TTY/non-TTY output. Local executable checks below exercise non-TTY output;
they do not claim a new interactive TTY walkthrough.

## Release-monitor correction

The generated issue incorrectly reported no directly relevant skill note:
`releaseHighlights` recognized “skill install” but missed “skill installation.”
Its matcher accepts explicit `gh skill` commands, including wrapped whitespace,
or a skill mention with an `install` stem (installed/installing/installation) in
the same note. Bare update/preview wording is ambiguous and requires gh context.
Adjacent bullets remain separate. These are potential-review highlights; the
matcher cannot establish semantic relevance or compatibility.
The exact 2.103.1 release-note regression and follow-up wording/ambiguity cases
failed before their respective fixes.

The follow-up Node run passed **23 tests**: 9 in
[critical-dependencies.test.js](../.github/scripts/critical-dependencies.test.js),
3 in [critical-dependency-assessment.test.js](../.github/scripts/critical-dependency-assessment.test.js),
and 11 in [ci-change-scope.test.js](../.github/scripts/ci-change-scope.test.js).
Reproduce it with:

```sh
node --test .github/scripts/critical-dependencies.test.js .github/scripts/critical-dependency-assessment.test.js .github/scripts/ci-change-scope.test.js
```

## Executed verification

Local checks used Linux ARM64, SDK **10.0.113**, runtime **10.0.13**, and managed
standalone/extension builds with locked dependencies. Downloaded gh archives were
verified against the SHA256 metadata of their official release assets. All
mutations used isolated HOME/config/state/CWD/Git fixtures; credentials stayed in
child-process memory and were never written to evidence.

The verified official Linux ARM64 archive SHA256 values were:

| gh version | SHA256 |
| --- | --- |
| 2.97.0 | `73ea440ecad9c9e284429997ee6f93577bc6f7bc6fba357ef62c53ad8fb641a5` |
| 2.103.0 | `7f322df34e624d14f5ec0c4acb121fd6dfa14392fdbbd1066aa8e0a05ac3721e` |
| 2.103.1 | `25b1287471a73a3cca32e5d2d2798ae26abea0243db1b9e4a8c1be506fad1bfe` |

Hosted run [40025766669](https://github.com/harder/gh-skillview/actions/runs/40025766669)
completed successfully with `head_sha`
[`3e6b69e27b3663b2748d9ca9e1c919b46fdc895e`](https://github.com/harder/gh-skillview/commit/3e6b69e27b3663b2748d9ca9e1c919b46fdc895e),
confirmed from the run's [API metadata](https://api.github.com/repos/harder/gh-skillview/actions/runs/40025766669).

| Check | Result |
| --- | --- |
| Seven skill help outputs: root, search, preview, install, update, list, publish | Byte-identical across 2.97.0, 2.103.0 and 2.103.1 |
| `--agent` list | All 48 entries exactly match `InstallAgentCatalog` |
| Local opt-in contracts, gh 2.97.0 | 19 passed, no skips/failures |
| Local opt-in contracts, gh 2.103.1 | 19 passed, no skips/failures |
| [Hosted minimum lane](https://github.com/harder/gh-skillview/actions/runs/40025766669/job/120661456518) | Passed on merged main |
| [Hosted latest lane, explicitly overridden to 2.103.1](https://github.com/harder/gh-skillview/actions/runs/40025766669/job/120661456774) | Passed; log confirms gh 2.103.1 and 19 successful contracts |
| Sequential managed App/extension builds | Passed with zero warnings/errors |
| Standalone Doctor/search, preview at `v2.103.1`, pinned remote install and local import | Passed against 2.103.1 |
| Local/remote gh install warning output, captured directly from native stderr | One pre-install warning each; post-install review guidance retained. SkillView's [install JSON writer](../src/SkillView.Core/Cli/CliDispatcher.Install.cs) does not serialize the raw `StdErr` stream. |
| Built extension apphost via `gh extension exec`, with 2.97.0 first on PATH | Passed; Doctor reports extension invocation and the invoking 2.103.1 `GH_PATH` |
| Solution formatting verification and changed Markdown relative links | Passed |

The opt-in runs explicitly set `SKILLVIEW_CONTRACT_TESTS=true`; they are not
default-suite early returns. They cover help/JSON shapes, pinned and local
metadata, ownership locks, exact-copy updates, retained local changes, partial
outcomes and requested preview revisions.

Windows/macOS runtime behavior and new Native AOT publishes were not exercised
locally. Actual OAuth credential rotation and the interactive login hint were
source-reviewed rather than forced against a user's account. Preserve these
limits when citing this compatibility verdict.
