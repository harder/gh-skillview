# SkillView acceptance — WINDOWS

Commit: `e13495244c73fc4fd6c14d834de03692c5fd29dc`

Date: October 21, 2026 (America/Chicago)

Committed evidence: this directory contains the reports, command logs and proof JSON selected for sharing. Absolute paths below refer to the original Windows test machine. Private homes, retained state, binaries, SDKs and package caches are excluded. The clean-source observations describe the checkout before this results-only commit.

Machine: native Windows 11 build 26340, ARM64 hardware/OS and .NET; NTFS on C:.
Python: `py -3`, Python 3.13.5 at `C:\Python313\python.exe`, x64 PE executable under Windows ARM emulation. Git 2.55.0.windows.5 is also x64. gh 2.102.0 and .NET SDK 10.0.402 are ARM64. No WSL commands or results were used.

SDK: private installation at `C:\Users\kevin\Documents\Codex\skillview-rc-20261021-5ab74870\dotnet`. The machine's default 10.0.401 was unchanged.

Checkout: `C:\Users\kevin\Documents\Codex\skillview-rc-20261021-5ab74870\checkout`, fresh HTTPS clone, detached at the exact requested SHA. `v1.0.1` resolves to `d6a578535e912876cd6f57158a6396fc94c0f4d7`. Source status was empty before testing and after all checks; the final SHA still matches (source-final-head.log, source-final-status.log). Existing development changes were not edited, stashed, reset, or cleaned.

Builds: Release unit/integration binaries and both native ARM64 AOT product hosts. Native publishes ran sequentially using the committed CI recipe and locked restore. Standalone publish also treated IL2026, IL3050 and IL3053 as errors.

- Standalone: `native-app\skillview.exe` beneath this report directory.
- Extension: `native-extension\gh-skillview.exe` beneath this report directory.
- The extension binary is tested directly; this does not claim an installed `gh skillview` extension-dispatch test.

Overall: **FAIL / incomplete acceptance**. Successful native proofs do not waive test failures or incomplete full-suite coverage.

## Automated checks

Read `AGENTS.md`, `docs/one-off-testing-agent.md`, `docs/one-off-testing.md`, the PTY guide, usage and saved-update documentation at the requested commit. No product code, tests, dependencies, lock files or automation were changed.

The Python smoke command was `py -3 -m unittest discover -s tools/tests -v`: **7 tests, 6 passed, 1 explicit Windows skip**. The skipped alias-output test says directory-symlink creation depends on native Windows privilege; it is a platform skip, not an observed inability to create symlinks here. Actual file/directory symlink probing and later native symlink proofs succeeded. See `python-smoke.log`.

### First run, preserved

Command: `py -3 tools/test_once.py --expect-commit e13495244c73fc4fd6c14d834de03692c5fd29dc --live --aot --dotnet <private-sdk>\dotnet.exe --output C:\Users\kevin\Documents\Codex\skillview-rc-20261021-5ab74870\results`.

- Locked test-project restores and Release builds passed.
- Unit suite completed: **2725 total, 2386 passed, 260 failed, 79 skipped**.
- Integration suite: **9 passed, 0 failed, 0 skipped**.
- Both product AOT restores failed with NU1004. Consequently this runner invocation exercised no product host or CLI proofs.
- Runner exit: **1**. Original logs and `REPORT.md` are preserved under that initial results path.

The first output directory was beneath the existing empty `C:\Users\kevin\Documents\Codex\.git` directory. Native policy treats the marker as repository containment even though Git does not recognize it as a valid repository. This contaminated the private fixtures and accounts for repository-managed relocation/compact-remove refusals. The marker was not changed. A single focused compact-remove control passed in a new fixture outside that ancestor (`reproduce-repository-marker-control.log`: 1/1 passed).

### Corrected fixture run

Command: `py -3 tools/test_once.py --expect-commit e13495244c73fc4fd6c14d834de03692c5fd29dc --live --dotnet <private-sdk>\dotnet.exe --output C:\Users\kevin\AppData\Local\Temp\skillview-rc-e134952-20261021`.

This is one corrected fixture pass, not a retry-until-green sequence. The original failing run remains evidence. See this directory's `REPORT.md`, `report.json`, `unit-tests.log`, `unit-observations.json` and `skip-reasons.json`.

Locked restore and the Release unit build passed. The unit command then **timed out after 1200.32 seconds**, with **32 observed failed cases and 79 explicit skip notices**, and **no final test summary**. Passed/total counts for this run are therefore unknown. All 32 emitted failure blocks mention `SeSecurityPrivilege`. The runner killed test PID 9580 and parent dotnet PID 1020; both terminations are recorded in `unit-tests.log` and neither remained alive. Runner exit: **1**. It stopped as designed after the timeout, so this invocation did not reach integration, managed product builds, CLI proofs, or its final source-status step. The first run's integration result and the separate AOT proofs must not be mistaken for those missing corrected-run steps. The full-suite stall was not isolated to a particular test and was not reproduced in another full-suite run.

The corrected log's observed failures report complete SACL visibility requiring `SeSecurityPrivilege`. A focused fresh-process reproduction of `NativeExposureLinkTests.TargetBackedRoundTripRetainsExactNativeLinkOrExplicitlyRefusesUnsupportedMetadata` failed **3/3 cases**, with the same privilege message (`reproduce-native-link.log`, `reproduction.json`). No elevation, security-setting changes or weakened metadata guards were used. This finding describes test-process failures; it does not erase the successful separate native product proofs.

79 explicit skip notices were observed: Unix/POSIX/Linux/macOS-specific fixtures, ten opt-in Linux owner-subprocess cases, one opt-in authoring proof, and unsupported full-merge metadata cases. Exact reasons and counts are retained in `skip-reasons.json`. Default-suite early-return contracts are not claimed as opt-in real-gh contract coverage. AI clients and Docker evaluation were not launched.

### Supplemental native AOT commands and proofs

The runner's `dotnet restore ... --locked-mode -r win-arm64` changes the restore RID set and fails NU1004 against the committed four-RID lock files. The documented CI form works without editing them:

```text
<private-sdk>\dotnet.exe publish src/SkillView.App/SkillView.App.csproj -c Release -r win-arm64 -p:PublishAot=true -p:StripSymbols=true -p:RestoreLockedMode=true -o <results>\native-app -warnaserror:IL2026 -warnaserror:IL3050 -warnaserror:IL3053
<private-sdk>\dotnet.exe publish src/SkillView.GhExtension/SkillView.GhExtension.csproj -c Release -r win-arm64 -p:PublishAot=true -p:StripSymbols=true -p:RestoreLockedMode=true -o <results>\native-extension
```

Both passed. Exact commands/durations: `native-check.json`; logs: `native-app-publish.log`, `native-extension-publish.log`.

Each existing proof was invoked with native Windows Python (`py -3` launcher, whose `sys.executable` runs each proof), the actual published AOT binary, installed gh, private HOME/config/state/temp/CWD and isolated Git/agent overrides. Authentication was captured before isolation and passed only in child memory/environment. It was not logged or saved.

**Both AOT hosts passed all six offline and both live proofs: 16/16 proof commands passed, plus 2/2 version checks.** No native proof was skipped. The six offline proofs were static-package-check, native-object-evidence, gitfile-protection, inventory-filter, managed-discovery and quarantine-undo. The two live proofs were revision-update and recorded-replacement. Exact commands/statuses/durations: `native-proofs.json`; per-host evidence: `native-app-*.json`, `native-extension-*.json`, with matching `.log` files. Existing gh authentication and remote requests worked for those fixtures.

The live revision proof recorded `cli/cli`, `skills/gh-skill`, tag `refs/tags/v2.103.0`, commit `7948e8056e45ac3c729b829e0eafe2d0797a95c6`, tree `a209a7ed2e72622b245a6bcb2dc2711a2ea5fb6b`, against original commit `33789149b9def6b66ddf247438a79fac94e22074`. It checks explicit unpin, reviewed local changes/extras, original native-directory restoration, approved stage promotion, unchanged registry/independent copies, and process-kill restart recovery/Undo for ordinary and namespaced targets. See `native-*-revision-update.json`. This is CLI evidence, not terminal interaction evidence.

Native x64 product builds, Linux/macOS native behavior, dedicated opt-in gh contract suites and frozen old-native compatibility matrices were not run in this bounded Windows pass.

## Terminal journeys

Host: Windows Terminal **1.25.2733.0 ARM64**, launched directly with `--size 80,24`, a dedicated title/tab and the published standalone AOT executable. A small child launcher explicitly set private HOME/config/state/CWD before starting the app; it did not expose an interactive shell. The recorded console dimensions were exactly **80 columns × 24 rows** (`tui-session-80x24.json`).

The application remained running and completed initial inventory subprocesses (`tui-startup.log`). This establishes native process startup, **not successful visual rendering or keyboard behavior**.

**All interactive terminal journeys are BLOCKED.** The installed Windows Computer Use API's `list_windows()` and `list_apps()` returned no target for the dedicated terminal, even though shell process information showed its title and live app child. A second explicitly visible launch had the same limitation. No supported window object could be selected, so no input was injected, no alternate raw input framework was built, and no screenshots were obtainable. This is an automation-tool limitation, not a confirmed SkillView rendering defect.

| Journey | Actual coverage |
| --- | --- |
| Start, browse, search, preview, focus, Tab/help/quit | Process startup/inventory logged; visual and input checks blocked. |
| Install and inspect copies | Two old pinned copies created with raw gh in the shell, one independently edited; shared directory link created. No TUI installation or inspection occurred. |
| Review a specific tag, cancel, approve/apply; pinned protection | TUI path blocked. Separate CLI revision proof passed; that is not a TUI or one-time version-choice proof. |
| Undo review, cancel, approve, stale approval | TUI path blocked. Separate CLI update/replacement/quarantine Undo proofs passed. |
| Cleanup review/cancel/apply | Empty directory and broken-link fixtures created beside healthy skills; no TUI cleanup performed. |
| Larger-size repeat, resize/reflow, clean quit | Not run: no targetable window/input path. No clipping claim is made without a capture. |

Fixture manifests before and after (`tui-before.json`, `tui-after.json`, `terminal-automation.json`) confirm unchanged payload bytes, observed native file identities, shared/broken-link targets and global gh registry hash. Fixture creation is distinct from product actions: **0 input actions, 0 TUI mutations, 0 screen captures**. Both owned terminal/app trees were stopped through shell process tools. Keyboard quit and graceful app teardown were not tested; forced termination is recorded in `tui-process-cleanup.log`.

## Findings, most important first

1. **Unresolved test/runtime failure: corrected full suite stalled and timed out.** Reproduce the corrected-run command above in an isolated location outside any ancestor `.git` marker. Expected: a final test summary. Actual: 32 failures and 79 skip notices, then no completion by the existing 20-minute deadline. Exact responsible test/root cause remains unknown; the stall was not independently reproduced or diagnosed. This prevents release-candidate acceptance.
2. **Native test-process metadata prerequisite failures.** Three focused link cases reproduced the complete-SACL/`SeSecurityPrivilege` failure in a fresh isolated process; 32 cases emitted that message during the corrected full run. Expected positive native metadata evidence was unavailable to those tests. No blanket platform pass, privilege escalation or safety-guard bypass is claimed. Separate published AOT product proofs succeeded and must be kept distinct from this failure.
3. **Testing harness issue: AOT restore command.** Both kit product restores fail NU1004 with `-r win-arm64`; both documented CI publishes pass locked restore and native compilation. Preserve the original logs; fix the kit in a separate change if desired. This pass made no fix.
4. **Initial fixture/environment error: ancestor `.git` marker.** Initial output under `Documents\Codex` made many fixtures repository-managed. A compact-remove control passed outside that ancestor. The original 260 failures remain visible but are not all asserted to be product defects.
5. **Tool limitation: native terminal not targetable.** The app started at 80×24, but neither native window inventory exposed its window. All interactive acceptance evidence remains missing. There is no screenshot path to return.

The successful CLI proofs explicitly checked retention/Undo, pinned refusal or unpin review, wrong fingerprints, unchanged independent peers and original native identities. No wrong-target mutation or lost fixture edits was observed there. The missing TUI journeys leave their approval, targeting, local-edit messaging and Undo UX unverified.

No finding is called pre-existing: no v1.0.1 baseline execution was needed or performed. The tag is available for later comparison.

## Remaining manual checks

Use the existing isolated terminal fixture and the exact AOT binary, with the same private environment as the launcher. Do not use actual user installations.

1. At 80×24, verify startup rendering, retained query focus, search/preview, Tab, help, shortcuts and Ctrl+Q. Repeat clipped views at a larger size and check resize/reflow.
2. Install with real gh through the TUI into a new empty fixture destination. Inspect the two independent old copies and shared consumer; confirm exact physical paths and workspace/scope after refresh.
3. In Updates choose an existing explicit tag, cancel first, then separately approve source/retention and apply only the selected copy. Verify payload hashes, registry/link evidence and unchanged independent peers. Confirm a pinned fixture remains protected until separately reviewed unpin.
4. Inspect the successful saved operation, cancel Undo, then approve that exact reviewed Undo. Verify original bytes/native identity and refresh; if time permits, alter the fixture after inspection and confirm stale approval refuses.
5. Review/cancel cleanup, then remove only the empty leftover and broken link. Verify healthy skills, shared-link targets and the independent peer remain unchanged. Confirm review-only items are explained without counting them as attempted deletions.

The test-process SACL failures and full-suite timeout also require diagnosis on native Windows. Do not assume the AOT CLI proof passes resolve them. Repeat integration/managed-host acceptance only when needed to complete the interrupted corrected-run coverage.

## Evidence and private fixture locations

- Main runner report and this agent report: `C:\Users\kevin\AppData\Local\Temp\skillview-rc-e134952-20261021`.
- Original failing runner evidence: `C:\Users\kevin\Documents\Codex\skillview-rc-20261021-5ab74870\results`.
- Dedicated checkout and private SDK: `C:\Users\kevin\Documents\Codex\skillview-rc-20261021-5ab74870`.
- Private terminal HOME/config/state/workspace: `C:\Users\kevin\AppData\Local\Temp\skillview-rc-e134952-private-tui`.
- Runner private homes, caches and fixtures are subdirectories of each results directory. Supplemental native proof and reproduction homes are under `private-native`, `private-native-proofs` and `private-reproduction`. Existing proof scripts remove some temporary fixtures themselves; their JSON/logs remain.
- Test utilities used only for this pass are outside the checkout beside `tui-fixture.py`, `native-check.py`, `native-proofs.py` and `reproduce.py`.

A shareable evidence-only copy is at `C:\Users\kevin\Documents\Codex\skillview-rc-20261021-5ab74870\share`, with archive `C:\Users\kevin\Documents\Codex\skillview-rc-20261021-5ab74870\skillview-windows-e134952-evidence.zip`. It contains only top-level reports/logs/proof JSON and the four small test-collection utilities, plus explicitly selected initial-run reports/logs. It excludes all private subdirectories, retained state, homes, binaries, SDKs and caches. No screenshots were obtained. All owned processes, including the private SDK compiler server, were stopped; no owned process remained that could not be stopped.

Do not upload the entire result directories, private homes, state archives, SDK, package caches or retained payloads. No files were uploaded during this task.
