# SkillView acceptance — MACOS

Commit: e13495244c73fc4fd6c14d834de03692c5fd29dc  
Date: 2026-10-21 (America/Chicago)  
Machine: MacBookPro18,2, Apple M1 Max (10 CPU cores), 64 GB RAM; macOS 26.7.1 (25G241), APFS. Hardware/process/Python/.NET/native products: **ARM64**. `sysctl.proc_translated=0`; no Rosetta. Python 3.15.0, private .NET SDK 10.0.402 (host 10.0.13, osx-arm64), gh 2.102.0, Apple clang 21.0.0/Xcode already installed. Terminal: real POSIX PTY with xterm-256color identity and the repository's probe/ANSI helper; parent terminal TERM=dumb.  
Builds: managed ARM64 unit/integration test hosts; native ARM64 AOT standalone and extension. Both products report `skillview 1.0.1 (Terminal.Gui 2.5.0.0)` because the candidate retains its default version.  
Overall: **FAIL**, with substantial passing supplemental native/remote/terminal coverage. Startup focus, one unit assertion, and the requested AOT runner path fail. No product source, dependencies, lock files, release, user installations, or automation were changed.

## Automated checks

Dedicated fresh HTTPS clone: `/Users/kharder/source/rc-tests/gh-skillview-e134952-20261021-175535`, detached at the exact requested SHA. Source status was empty before and after testing, including untracked files. `v1.0.1` is present; its peeled commit is `d6a578535e912876cd6f57158a6396fc94c0f4d7`. The original development checkout was left untouched. Read the candidate's AGENTS.md, one-off testing guides, PTY guide, usage, saved-update guide and relevant release instructions.

The exact SDK was absent from the machine's standard installation. Installed the official SDK into `/Users/kharder/source/rc-tests/sdk-10.0.402-arm64`; no system SDK/default was changed. Python HTTPS initially hit a CA key-usage validation error fetching the installer; ordinary verified curl fetched the official script successfully. No certificate checks were disabled.

Commands from the dedicated checkout:

```text
python3 -m unittest discover -s tools/tests -v
python3 tools/test_once.py --expect-commit e13495244c73fc4fd6c14d834de03692c5fd29dc --live --aot --dotnet /Users/kharder/source/rc-tests/sdk-10.0.402-arm64/dotnet --output /Users/kharder/source/rc-tests/acceptance-e134952-macos/run
```

- Python smoke: **7 passed**.
- Unit build: 0 warnings/errors. Unit tests: **2,725 total; 2,688 succeeded, 1 failed, 36 skipped**, 3m46s. Failure reproduced once with the exact method query: 4 theory cases, 3 passed/1 failed.
- Integration build: 0 warnings/errors. Integration tests: **9/9 passed**, no skips.
- Requested runner exit: **1**. Both RID-specific locked product restores failed NU1004. Runner `hosts` is empty: **no offline or live product proof ran inside the original runner**, despite `--live`. Original REPORT.md/report.json/logs are preserved unchanged.
- The kit did not enable opt-in gh contract, authoring runtime or PTY suite bodies; some return early and are counted successful, so success totals are not proof those bodies executed.

Explicit skips (36): Linux-only injected evaluation/native staging 9; authoring proof tools opt-in 1; Windows held-parent raw target 1; Windows held-parent generation 4; Linux `/usr/bin/env` fixture 1; Windows native raw-target/follow parity 10; Linux native owner subprocess opt-in 10. See `skips.txt` and the individual unit log reasons. Windows/Linux branches returning early are not native Mac coverage.

### Supplemental checks, separate from the failed kit

Kept the committed lock files unchanged. A normal `dotnet restore --locked-mode` passed, then sequential `dotnet publish PROJECT -c Release -r osx-arm64 --self-contained true --no-restore -o OUTPUT` published both hosts successfully. This uses the existing all-RID restore followed by native publish, avoiding the kit's single-RID locked-restore mismatch. No failing test was waived or changed; original runner remains failed. Exact argv, outcomes and durations are in `supplemental/commands.json`.

Actual product binaries:

- `/Users/kharder/source/rc-tests/acceptance-e134952-macos/supplemental/app/skillview`
- `/Users/kharder/source/rc-tests/acceptance-e134952-macos/supplemental/extension/gh-skillview`

`file` confirms both are Mach-O ARM64. Both passed `--version`. No managed product apphost was used for the acceptance proofs. The extension executable was invoked directly; installation/registration and launch through `gh skillview` were not exercised.

**All 16 supplemental proofs passed**: each host passed static-package-check, native-object-evidence, gitfile-protection, inventory-filter, managed-discovery, quarantine-undo, live revision-update and live recorded-replacement. Existing gh credentials were captured privately before HOME isolation and passed only through child environments. Remote fixtures used actual gh and public repositories. Native identity, retained original, registry, unchanged-peer and process-kill/restart assertions passed where present in those committed scripts. Review each proof's JSON for its actual assertions and coverage limits; these are not general platform/security attestations.

Logs/command manifests: `/Users/kharder/source/rc-tests/acceptance-e134952-macos/supplemental/`. Original command report: `/Users/kharder/source/rc-tests/acceptance-e134952-macos/run/REPORT.md`, `/Users/kharder/source/rc-tests/acceptance-e134952-macos/run/report.json`.

## Terminal journeys

Reused the committed `agent_docs/evidence/prove-reviewed-update-tui.py` PTY/probe/render helpers in an **external test-only adaptation**, leaving the checkout untouched. Started at **80×24**, then resized the same real PTY to **120×40** for cramped path/review views. Keys, SGR mouse events and startup terminal responses went through the PTY. Screen text captures come from the small existing ANSI reader and may retain stale cells after partial redraw; raw transcripts and filesystem/log observations take precedence over apparent pixel defects. Harness selector timeouts/one overlong outer terminal input were corrected as harness issues, not counted as product failures.

| Journey | Outcome and evidence |
| --- | --- |
| Start, query focus, quit | **FAIL for initial focus.** First fresh launch typing `gh-skill` left Discover unexpectedly. One focused fresh-fixture reproduction waited for startup, then typed `focusprobe` without clicking or Tab: Installed opens with filter `ocusprobe`, instead of Discover query `focusprobe`. Both sessions quit using Ctrl+Q with code 0. `startup-repro.focus.json`, `startup-repro.startup-focus-probe.txt`, `startup-repro.transcript`. |
| Browse, search, preview, help | **PASS after explicitly focusing query via `/`.** Live `gh-skill` search returned 15 results, selected first row and Enter loaded its preview; F1 displayed scrollable key help at 80×24. Tab/key/mouse paths were exercised; initial query focus remains a separate failure. Search failure/offline messaging was not exercised. Captures `terminal.search-results-80x24.txt`, `terminal.preview-request-80x24.txt`, `terminal.f1-help-80x24.txt`; app log records preview success. |
| Install and inspect copies | **PASS for tested paths.** Initial fixture `cli/cli/skills/gh-skill` was installed using raw actual gh at pinned ref `33789149b9def6b66ddf247438a79fac94e22074` (fixture creation, not a TUI install). Added a manually copied differing Claude peer and a Codex shared symlink. Inventory showed independent physical rows/consumers. Later TUI Discover → skill-creator → compact install: canceled first, reopened, cleared detected Claude/Codex checkboxes, then installed through actual `gh skill install --agent universal --scope user`. Child exit 0, `.agents/skills/skill-creator/SKILL.md` exists, registry contains `gh-skill` and `skill-creator`; existing payload manifests stayed unchanged. Public source `composio-community/awesome-codex-skills`; no skill scripts/AI clients executed. `terminal-app.log`, install captures, `terminal.evidence.json`. |
| Pin and exact version/update | **PASS.** Pinned version selection refused with separate-unpin guidance at 80×24. Separate unpin review: skipped first (bytes/registry unchanged), then approved retaining original and applied unpin only. At 120×40 selected the shared gh-skill row, prepared exact `v2.102.0`, skipped review and verified no mutation, prepared again, checked F2 frozen source details/omissions, verified Enter without approval did not apply, then checked source review and retention and selected Replace reviewed. Candidate/native manifest matched; retained original preserved local extra bytes and original native objects; differing independent peer, unrelated skill and shared link stayed unchanged. |
| Undo | **PASS.** Actual TUI Undo inspection → Cancel undo preserved applied payload and registry. Fresh inspection → separate restoration approval → Undo update restored the exact pre-update native manifest and registry bytes. Independent peer/unrelated skill/link unchanged. Refresh succeeded after completion. Stale-after-inspection editing was not attempted. `terminal.undo-inspection.txt`, `terminal.undo-canceled.txt`, `terminal.undo-final-result.txt`. |
| Cleanup | **PASS.** Selected broken link, empty directory and one comparison-only row. Review/cancel preserved everything. Reopened review and Remove deleted only the safe two entries; final UI **removed 2, skipped 1, failed 0**. Healthy payload, independent peer, unrelated skill, registry and shared link unchanged. `terminal.cleanup-review-full.txt`, `terminal.cleanup-canceled.txt`, `terminal.cleanup-final-accounting.txt`. |

Exact tag resolved to commit `fc4b137cdef0a6bd28fd461b7cf9c84a5812a8cd`, tree `a209a7ed2e72622b245a6bcb2dc2711a2ea5fb6b`, subpath `skills/gh-skill`. Reviewed update plan: `1a417670e7624123a288e2ed8aca0a85`. Raw bytes, object IDs/modes/link targets were checked through the inherited helper's manifest assertions; owner registry bytes were compared before/after cancel and Undo. This is not a complete ACL/xattr attestation. Supplemental native proofs add their documented evidence.

## Findings, most important first

1. **Product / startup UX — initial query does not retain focus.** Reproduction: fresh native ARM64 AOT app in private fixture, 80×24 xterm PTY; wait for startup, type `focusprobe`. Expected Discover query to contain the whole string; actual Installed filter is `ocusprobe`. The first `f` appears to route as a shell action. Reproduced once in a second fresh fixture; no baseline comparison, so not labeled pre-existing. Explicit `/` allows search. No destructive action occurred. Capture/log paths above. This violates the requested startup-focus acceptance condition.
2. **Test kit / build — `--aot` cannot produce hosts with its current locked restore invocation.** Exact commands in original report use `dotnet restore PROJECT --locked-mode -r osx-arm64`; both hosts report NU1004: project RID set `osx-arm64` differs from committed `linux-x64;osx-arm64;win-arm64;win-x64`. Expected kit to publish/prove hosts; actual both proof groups not run. Supplemental all-RID locked restore/native no-restore publish succeeded with unchanged lock files. This is a kit-path failure, not evidence the native products cannot build.
3. **Test fixture — collision guard assertion fails in this checkout path.** `GhSharedLockGuardTests.NamespacedAliasesAndTruncatedListingCannotBypassCollisionGuard(requested:null,truncate:true)` expects `truncated`, receives `Git project-root discovery is incomplete; ...`. Focused reproduction: same failure among four theory cases. The fixture caps captured streams at 64 characters; this checkout's Git-root output is 68 characters. **Inference:** discovery is truncated before the intended gh listing seam. Actual operation refuses; no evidence of an overwrite bypass or lost edits. Original failing log and focused reproduction are retained. No fix applied.
4. **Coverage limits / harness.** Terminal screen extraction is approximate, not a pixel-perfect emulator. Some waits initially observed stale labels before owned completion; final mutation claims rely on completed UI state plus native manifests/logs. No additional confirmed product loss, wrong-target mutation, misleading retention approval, or missing Undo was observed in the tested journeys.

## Remaining manual checks

- Verify initial query focus and the same journeys in the user's actual terminal app, including visual layout/accessibility at 80×24; the PTY focus failure remains unresolved.
- Launch a separately installed extension through the real `gh skillview` host; only the extension binary itself was proved here.
- Offline search error messaging, workspace/profile context switching, stale Undo approval after editing, and full keyboard-only traversal were not completed. Default context refresh and mixed key/mouse journeys were tested.
- Windows native/Intel/Rosetta/Linux-only cases, Docker isolation, special metadata privilege cases, and opted-out authoring/gh contract suites were not covered on this Mac. No ABI checks were bypassed. Native macOS ARM64 operations succeeded, so unsupported Intel ABI refusal is not applicable here.
- Formatting and an exhaustive agent matrix were not run; this was the bounded one-off task, not a broad review.

## Artifacts, privacy and cleanup

Full result root: `/Users/kharder/source/rc-tests/acceptance-e134952-macos`. **Do not upload it wholesale**: original/supplemental runners retain private HOME/config/state/temp trees and AOT binaries. Share `SHAREABLE.zip` or the curated `shareable/` directory only; they contain reports, command logs/JSON, selected screen text and PTY transcripts, excluding fixture homes, state archives, package caches, SDK and binaries. No upload was performed by this task.

Private terminal fixture locations (left locally for investigation):

- `/private/var/folders/f_/4f2gwv6x5qzflv1rg915_c740000gp/T/skillview-acceptance-tui-ypfnvige` — update/Undo/cleanup/install fixture, including retained plans.
- `/private/var/folders/f_/4f2gwv6x5qzflv1rg915_c740000gp/T/skillview-acceptance-tui-vze71ric` — fresh startup-focus reproduction.

Runner isolation trees are under `/Users/kharder/source/rc-tests/acceptance-e134952-macos/run` and `/Users/kharder/source/rc-tests/acceptance-e134952-macos/supplemental` (home/config/state/tmp/etc.). Individual committed proof scripts automatically removed their own TemporaryDirectory fixtures; their command logs and JSON remain. Fixture-only auth tokens were held in memory/child environments, not saved in commands or reports. No private fixture homes are included in the shareable bundle.

All owned runner/proof/TUI processes stopped; both TUI exits were 0. See `process-cleanup.log`. Dedicated checkout remains clean at the exact commit (`final-checkout.log`). No features/fixes, pushes, PR changes or recurring automation were made.
