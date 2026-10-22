# Focused SkillView macOS confirmation

**Result: PASS for this focused task.** Date: 2026-10-21 (America/Chicago).

Base commit: `99409ee9220736854dfaf3b8aa6ecc7c69ec8252`. Branch: `codex/macos-acceptance-confirmation`.

Fresh separate worktree: `/Users/kharder/source/rc-tests/gh-skillview-macos-confirmation`. Read AGENTS.md and [the original follow-up task](../one-off-macos-2026-10-21-e134952/FOLLOWUP.md). The worktree was clean before results were added. Existing worktrees, the original report and all its evidence remain unchanged.

## Scoped tests

Used the existing private .NET SDK **10.0.402**, host/runtime **10.0.13**, ARM64, at `/Users/kharder/source/rc-tests/sdk-10.0.402-arm64/dotnet`. No SDK install, package/lock change or product change. MacBookPro18,2, native ARM64 hardware and Python process, macOS 26.7.1, `sysctl.proc_translated=0` (no Rosetta).

Restored in locked mode and built Release sequentially: unit restore → unit build → integration restore → integration build. Both builds had zero warnings/errors. Then ran exactly the two FOLLOWUP.md filters with `--no-build --no-restore`:

- `/*/SkillView.Tests.Maintenance/GhSharedLockGuardTests/NamespacedAliasesAndTruncatedListingCannotBypassCollisionGuard`: **4 succeeded, 0 failed, 0 skipped**.
- `/*/SkillView.IntegrationTests.Ui/SkillViewAppIntegrationTests/RunAsync_DiscoverStartupTypingReachesQueryWithoutFocusShortcut`: **2 succeeded, 0 failed, 0 skipped**, covering 80×24 and 120×40.

Exact argv, times and exit codes: [commands.json](commands.json). Logs: [collision-tests.log](collision-tests.log), [startup-typing-tests.log](startup-typing-tests.log), and the four restore/build logs. No full suite or remote proof matrix was run.

## Native PTY confirmation

Reused the **existing native ARM64 AOT standalone binary**:
`/Users/kharder/source/rc-tests/acceptance-e134952-macos/supplemental/app/skillview`.
SHA256: `6cad7a2bb4849f62b610b7b40309e2c24893e65844b806013238880144abb476`.
This binary was built at `e134952`; `git diff --quiet e134952 99409ee -- src` returned 0, confirming unchanged product source. New test code ran at the requested base, while the PTY used the original native executable as requested. No product rebuild/publish was performed.

Reused the prior PTY helper's probe responses, ANSI reader, send/read/wait functions in [focused-pty.py](focused-pty.py), an external test-only adaptation retained as executed. Real POSIX PTY, `TERM=xterm-256color`, **80×24**, actual keyboard bytes. Fresh separate fixture HOME/config/state/temp/CWD per case; inherited Git, gh and agent overrides stripped, private agent homes set. No remote installation or authentication token was needed. Populated fixture was one manually authored benign SKILL.md, not a real user installation.

Waited for filesystem inventory count, gh inventory completion and the final gh-version status, then drained terminal output for two seconds before recording the active view. This replaces the original first-frame/initial-ready assumption. Captures record the state immediately before typing; action records include monotonic times and raw transcript byte offsets.

| Case | Settled state and input | Result |
| --- | --- | --- |
| Empty inventory | Discover active at 3.058s; sent `focusprobe` directly, with no click, Tab, `/` or focus API. | **PASS:** Discover query shows the complete `focusprobe`; Ctrl+Q exit **0**, process stopped. |
| Populated inventory | Installed active at 2.92s; sent `/` from its list, recorded Discover before typing, then sent `focusprobe`. | **PASS:** Discover query shows the complete `focusprobe`; Ctrl+Q exit **0**, process stopped. |

Evidence:

- Empty: [settled before input](empty-settled-before-input.txt), [after typing](empty-after-typing.txt), [raw transcript](empty.transcript), [structured result](empty.json), [app log](empty-app.log).
- Populated: [Installed before input](populated-settled-before-input.txt), [Discover after slash](populated-slash-before-typing.txt), [after typing](populated-after-typing.txt), [raw transcript](populated.transcript), [structured result](populated.json), [app log](populated-app.log).

No unresolved reproduction in these two requested settled-state paths. The corrected collision fixture also passes natively on this Mac. The new evidence supports the FOLLOWUP.md explanation: startup may initially paint Discover and later switch to Installed for populated inventory; `f` from Installed is a filter shortcut. This does **not** establish a repaired product focus defect, since no product focus change was made, and does not retroactively change the original report's observed failure/status. Early typing during delayed startup inventory was not tested here. Windows permission-sensitive failures and other original missing coverage remain separate.

## Preservation and privacy

All owned test and PTY processes completed; both native PTY results record `processStopped: true`. No mutations to actual user skills, product code, package locks, original reports or automation. Only this new confirmation evidence directory is committed.

Private fixtures remain locally for investigation, excluded from Git/uploads:

- Empty: `/private/var/folders/f_/4f2gwv6x5qzflv1rg915_c740000gp/T/skillview-focused-empty-q9op6y6m`
- Populated: `/private/var/folders/f_/4f2gwv6x5qzflv1rg915_c740000gp/T/skillview-focused-populated-44to_ck3`
- Build/test isolation: `/Users/kharder/source/rc-tests/macos-confirmation-99409ee` (contains private home/config/state/temp directories; do not upload wholesale).

This directory includes only selected reports, command logs, structured outcomes, text/raw terminal captures and the credential-free test adaptation. It excludes private fixture trees, state archives, package caches, SDK and native binaries. Text snapshots use the existing approximate ANSI reader; raw transcripts retain terminal identity/probes, active views and query output. [MANIFEST.sha256](MANIFEST.sha256) covers the committed evidence bytes. No recurring automation or remote proof matrix was run. Completed within the requested 20-minute bound.
