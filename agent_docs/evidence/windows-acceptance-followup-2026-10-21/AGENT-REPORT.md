# Focused SkillView Windows acceptance follow-up

Base: `99409ee9220736854dfaf3b8aa6ecc7c69ec8252`

Branch: `codex/windows-acceptance-followup`

Date: October 21, 2026, America/Chicago. Work began at 20:25 CDT.

**Outcome: fixture bug fixed and verified; Windows acceptance remains incomplete.** Missing SACL rights still fail the positive native proofs. Native terminal startup ran, but the existing automation tools still could not target either terminal window for input or capture.

The [original report](../one-off-windows-2026-10-21-e134952/AGENT-REPORT.md), its [follow-up analysis](../one-off-windows-2026-10-21-e134952/FOLLOWUP.md), and all original artifacts are preserved unchanged. This pass did not rerun a full suite or any of the 16 passing CLI proofs. It changed one test fixture helper, added three regression cases, and recorded the durable lesson in `AGENTS.md`. No product source, security guards, positive-proof assertions, dependency locks, system security policy or user installations changed. No elevation was requested or used.

## Permission investigation

The first test execution after locked restore/build was the requested three-case `NativeExposureLinkTests.TargetBackedRoundTripRetainsExactNativeLinkOrExplicitlyRefusesUnsupportedMetadata`, with `SKILLVIEW_EXPOSURE_LINK_PROOF=1`. Fresh native Windows fixtures reproduced **3/3 SeSecurityPrivilege failures**, with no relative-open access-denied failure. See [baseline-focused.log](baseline-focused.log).

The later diagnostic run's access-denied failures are a separate, reproducible fixture problem. A new private parent was given inheritable SYSTEM, Administrators and OWNER RIGHTS FullControl grants. Before the owner setup call, its directory link was already owned by the effective user and readable/deletable through a no-follow open. Calling the old helper's `SetSecurityInfo(OWNER_SECURITY_INFORMATION)` with that **same owner SID** returned success, but changed the link's DACL on this machine:

```text
Before: D:(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)(A;OICIID;FA;;;OW)
After:  D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;IO;FA;;;OW)
```

The OW entry became inherit-only rather than granting access to the object itself; the DACL became protected. New no-follow read-attributes, delete-access and write-owner opens changed from success to Win32 error 5. The owner stayed the current user. The parent and target descriptors were unchanged. An ordinary parent with inherited explicit user grants did not show this transition. The observation used a held, no-follow security handle so the post-change descriptor could still be inspected without reopening the denied name. Account/capability SIDs in the shared [ACL probe](acl-probe.json) are replaced by role labels.

This reproduces the fixture restriction described in the earlier `FOLLOWUP.md`. It does **not** explain away missing SACL privileges, prove that every earlier access denial shared this cause, or establish a production defect.

### Verified fix

`WindowsPayloadMetadataTests.SetFixtureOwner` now reads the owner through a no-follow handle and returns without writing when it already matches the effective user. The existing different-owner normalization path remains. Thus ordinary already-owned files, directories and symbolic links keep their entire security descriptor and access instead of undergoing a redundant owner mutation.

The new `AlreadyOwnedFixtureKeepsInheritedOwnerRightsAndNoFollowAccess` regression has file, directory and directory-link cases. It establishes current-user ownership before installing the OWNER RIGHTS scenario, so elevated CI's initial Administrators ownership does not invalidate the setup. It asserts no-follow access and byte-identical security descriptors after owner normalization. A held DACL handle permits fixture cleanup even against the old broken helper.

The **exact final regression** failed **3/3** with access error 5 when rebuilt against the original helper, and passed **3/3** with the fix. The temporary baseline edit was restored byte-for-byte before the final build. See [final-owner-regression-before.log](final-owner-regression-before.log) and [final-owner-regression-after.log](final-owner-regression-after.log). Earlier reproduction logs are also retained.

No inherited ACL was relaxed to make product code pass. Only new disposable fixture parents were configured to reproduce the problem; production permissions and proof gates remain intact. The different-owner/elevated normalization branch was retained but was not separately exercised under elevation in this pass.

## Focused verification

All execution used native Windows 11 ARM64, Windows Python via `py -3` (win-amd64 build under Windows ARM emulation), native ARM64 .NET, and the existing private **SDK 10.0.402**. There was no WSL controller or execution. Exact command arguments, timeouts and durations are in the matching JSON files; logs contain the actual summaries. Fixtures used fresh isolated HOME/config/state/temp directories outside repository-marker ancestors.

| Run | Passed | Failed | Skipped | Interpretation |
| --- | ---: | ---: | ---: | --- |
| Requested three-case baseline | 0 | 3 | 0 | Missing SeSecurityPrivilege. |
| Exact final new regression, original helper | 0 | 3 | 0 | Confirmed fixture error 5 before the fix. |
| Exact final new regression, fixed helper | 3 | 0 | 0 | All three fixture object kinds preserved. |
| Requested three-case proof after fix | 0 | 3 | 0 | Positive metadata requirement still fails on missing privilege. |
| Requested proof under OWNER RIGHTS temp parent after fix | 0 | 3 | 0 | Reaches the SACL requirement; no access-denied or cleanup error. |
| Ordinary explicit-refusal contract | 3 | 0 | 0 | Refusal and unchanged-payload assertions passed; **not** successful retention/publication proof. |
| NativeExposureLinkTests class | 24 | 16 | 8 | All 16 failures mention SeSecurityPrivilege; no access-denied failures. |
| WindowsPayloadMetadataTests class | 21 | 0 | 0 | Includes portable cases, new fixture regressions and unprivileged refusal paths. |
| StandaloneUnpinTests class | 20 | 9 | 0 | All 9 failures mention SeSecurityPrivilege; no access-denied failures. |
| Shared unpin torn-image/restart method | 0 | 4 | 0 | All 4 failures mention SeSecurityPrivilege; no access-denied failures. |

These are separate, sometimes overlapping runs, not an aggregate unique-test total. The eight class skips are four Unix raw-target trailing-separator cases and four Unix no-follow-resolution cases. Existing Windows-only/Linux-only early returns are not additional native-platform proof.

All normal focused runs retained `SKILLVIEW_EXPOSURE_LINK_PROOF=1`. Only the separately named `refusal-contract` invocation omitted that opt-in flag to exercise the existing unsupported-metadata/refusal branch; the strict runs remain failed and visible. `SKILLVIEW_WINDOWS_METADATA_PROOF` was not enabled for the ordinary metadata class, whose passing refusal tests cannot establish successful complete SACL publication.

Locked restore, the final Release build (zero warnings/errors), scoped formatting verification and whitespace checks passed. The fresh standalone ARM64 AOT publish passed with `RestoreLockedMode=true` and IL2026/IL3050/IL3053 treated as errors. That host was used for terminal startup; the 16 earlier CLI proofs were not repeated. See [build.log](build.log), [final-format.json](final-format.json), [publish.log](publish.log), and [focused-summary.json](focused-summary.json).

## Native terminal attempt

The existing Windows terminal testing skill and installed `@oai/sky` facilities were used. The previous small private fixture/launcher utility was reused with new output paths and the freshly published AOT host; no terminal input/capture framework was built.

Two copies were installed with actual gh into new private directories, one was independently edited, and shared/broken links plus an empty cleanup directory were created. These are **shell-created fixtures**, not TUI installations or cleanup actions. Credentials were captured privately before isolation and passed only in child environments.

| Host | Actual startup size | Result |
| --- | --- | --- |
| Windows Terminal 1.25.2733.0 ARM64 | 80×24 | Live AOT process and completed inventory subprocesses; no targetable window returned by Computer Use. |
| Terminal Preview 1.26.2734.0 ARM64 | 80×24 | Live AOT process and completed inventory subprocesses; no targetable window returned by Computer Use. |

`sky.list_windows()` returned no target for either dedicated title; `sky.list_apps()` returned the Terminal Preview app with an empty window list. These observations were repeated after startup. Shell process information established that the app was running, but could not substitute for a supported window object. No guessed handle, alternate input injection or unsupported capture was attempted.

**Search, preview, install, update review, Undo, cleanup, keyboard quit, visual rendering and resize acceptance remain blocked.** There were **0 input actions, 0 TUI mutations and 0 screenshots**. Startup logs are process/inventory evidence, not proof of correct visual startup. A larger-size visual repeat was not attempted without input/capture capability.

[Before](tui-before.json) and [after](tui-after.json) manifests show unchanged payload hashes, native identities, link targets and gh registry bytes. Owned app/wrapper processes and the owned stable-terminal process were stopped; this was forced cleanup, not a successful Ctrl+Q test. The pre-existing Terminal Preview server was preserved; an exited-session Preview tab may require manual closing. See [terminal-summary.json](terminal-summary.json), [tui-startup.log](tui-startup.log), and the per-host session/cleanup logs.

## Remaining blockers and manual checklist

1. **Unavailable rights:** strict native link retention and metadata-dependent unpin publication remain failed/blocked by absent SeSecurityPrivilege. Verify positive publication only in an already-admitted Windows context; no elevation or security-policy changes were made here.
2. **Terminal tool access:** manually test the disposable fixtures at 80×24, repeating clipped views at a larger size:
   - Confirm visual startup, query focus, search/preview, Tab/help and Ctrl+Q.
   - Install into an empty private destination; inspect independent copies/shared consumers and correct scope after refresh.
   - Review a specific existing tag, cancel, then approve source/retention for one exact copy; verify pinned protection and unchanged peers.
   - Inspect Undo, cancel, approve the exact reviewed Undo and check original bytes/identity; test stale approval if time permits.
   - Review/cancel cleanup, then apply only to the empty leftover/broken link; verify healthy skills and independent copies remain unchanged.
3. **Full-suite status:** no full suite was run in this focused pass. The earlier full-suite timeout remains unproven; the intervening diagnostic completion is preserved in the original follow-up. This fixture fix and these focused results are not a full-suite or release acceptance pass.

## Local evidence and exclusions

This checked-in directory contains only selected reports, command logs and JSON. [The manifest](evidence-manifest.json) records hashes of the evidence bytes. Original local results and disposable fixtures are at `C:\Users\kevin\AppData\Local\Temp\skillview-windows-followup-20261021`; private fixture trees begin with `private-`. The existing private SDK and collection utilities remain beside the dedicated checkout at `C:\Users\kevin\Documents\Codex\skillview-rc-20261021-5ab74870`.

Private HOME/config/state contents, retained archives/payloads, package caches, binaries, raw diagnostic environment dumps and credentials are excluded from the commit. Absolute fixture paths in logs are investigation locations, not uploaded fixture contents. Original development edits and earlier reports were not reset, stashed, cleaned or overwritten.
