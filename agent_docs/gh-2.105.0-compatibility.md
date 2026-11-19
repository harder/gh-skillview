# GitHub CLI 2.105.0 compatibility review

Reviewed November 19, 2026, from the previously inspected 2.103.3 through
[2.104.0](https://github.com/cli/cli/releases/tag/v2.104.0) and
[2.105.0](https://github.com/cli/cli/releases/tag/v2.105.0).
Recommend 2.105.0; retain the **2.102.0 security minimum**. No new skill feature
or command contract requires a higher floor.

## Complete source review

The [comparison](https://github.com/cli/cli/compare/v2.103.3...v2.105.0)
contains 150 commits. Reviewed every subject and first-parent changed-file list,
including merges and empty commits, then inspected the relevant implementation
diffs. The [ledger](evidence/gh-2.105.0-commit-review.json) records every SHA,
subject, file list and review area. It also retains the seven commits from the
older 2.103.2/2.103.3 issues so those alerts have an explicit disposition.

Both skill trees are identical at 2.103.1, 2.103.2, 2.103.3, 2.104.0 and 2.105.0:

| Directory | Unchanged Git tree SHA |
| --- | --- |
| `internal/skills` | `ebe70235f23e47faac50c7e7c0f64ab877cea084` |
| `pkg/cmd/skills` | `9f69d8c735207dc614a15330429781648a8cdb89` |

This covers search, preview, install/update/list/publish, JSON fields, discovery,
frontmatter, source selection, tracking metadata, all 48 agents and placements.
Actual minimum/latest help differs: pflag 1.1.0 now prints boolean options as
`--flag[=true|false]` and adjusts alignment. All seven skill help outputs match
after removing that annotation and normalizing whitespace. This does not turn
`--upstream` into a URL option or change SkillView's existing boolean arguments.

## Changes relevant to SkillView

| Change | Impact and decision |
| --- | --- |
| 2.104.0 auth-status underlying errors | Raw output now includes rate-limit, server and network diagnostics. Existing parsing preserves them and rejects nonzero results. Added source-derived error fixtures; no credential handling change. |
| 2.104.0 refresh with read-only state | gh skips an exchange it could not persist and emits a warning, including on successful commands. Keep refresh delegated to gh and success based on exit code. Added a successful-login/warning fixture. Do not force expiry, rotate credentials or classify every failure as expiry. |
| 2.105.0 `gh event` preview | Can help development automation wait for PR/review/check-suite events. It requires account enablement, is github.com-only and does not replay missed events. The current account returns an explicit preview-not-enabled 403. No product integration or minimum-version increase. |
| Agent-facing event hints | Hints go to stderr for PR API reads and PR/run commands. SkillView's API adapters use repository refs/trees/blobs; those endpoints do not match the hint. JSON stdout remains separate. |
| `skills/gh/SKILL.md` event guidance | Upstream skill content changed; the skill-command implementation did not. Existing preview and saved-update review can show this content without a new workflow. |
| Git client refactor | Removes test seams, extracts a push-ref parser and simplifies `-C` insertion. No skill command or extension dispatch change. Real isolated install/update/preview contracts pass. |
| Other changes | Private-fork deletion confirmation, opaque node-ID encoding, Go/terminal/dependency servicing, repository triage, evals and ghmock. SkillView does not invoke repo deletion or embed those tools; no new product API adoption is needed. |

The older 2.103.2 attachment-token change applies to issue/PR file uploads,
not skill-install quota. The 2.103.3 expiry hints apply after HTTP 401, not
403/503/network failures. Existing guards and fixtures remain intact.

## Executed verification

Official Linux x64 binaries were SHA-256 verified against release checksums.
Tests isolate fixture HOME/state/CWD and Git environment; authentication stays
with gh. No user skill installation was changed.

| Check | Result |
| --- | --- |
| Explicit opt-in contracts, gh 2.102.0 | 19 passed; zero failures/skips |
| Explicit opt-in contracts, gh 2.104.0 | 19 passed; zero failures/skips |
| Explicit opt-in contracts, gh 2.105.0 | 19 passed; zero failures/skips |
| Auth parser fixtures | 18 passed, including four new diagnostic/warning cases |
| Seven skill help outputs / agent catalog | Equivalent command surface / exactly 48 matching selectors |
| Built Native AOT standalone, gh 2.105.0 | Doctor, real search and versioned `skills/gh` preview passed; preview includes the new event guidance |
| Actual extension dispatch | Doctor reports `GhExtension` and exact 2.105.0 `GH_PATH` while 2.102.0 is first on PATH |
| Native-input offline contracts | 29 passed after fixing one unintended host-SDK dependency in a dirty-source fixture |

The contract suite exercises metadata, raw scan/list reconciliation, local
imports, pinned installs, recorded-base updates, local-change protection,
registry locking and partial-update outcomes. It is distinct from default-suite
opt-in early returns. No actual OAuth rotation or account-gated event delivery
was forced. Hosted and four-platform native qualification remain separate checks.

## Issue follow-up and fixes

Issues #93, #117, #142 and #160 track the reviewed releases. Issue #99's original
run failed rendered-detail assertions; its recent runs fail four stale detail
labels on both minimum/latest gh. The current source reproduces those four
failures. Updating the expected authored-version and upstream-link labels makes
all three explicit 19-case lanes pass while retaining raw metadata, JSON,
source/ref/pin and unchanged-file assertions. Do not relabel old failed runs.

CI's checksum-bound current gh host advances to 2.105.0 together with its transfer
receipt validator and offline fixtures. Frozen producer sources and SDKs remain
unchanged. The dirty-source fixture now mocks SDK selection consistently and
asserts its precise refusal instead of requiring SDK 10.0.403 on the host.
