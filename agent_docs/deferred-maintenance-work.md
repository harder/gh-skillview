# Maintenance scope inventory and deferred work

This is a practical prioritization ledger, not an acceptance checklist. The
former all-21 completion goal is retired. Implementation is limited to explicitly
approved practical work. SkillView should remain a practical skill manager:
prioritize useful, understandable repairs and simple user flows. Preserve useful
completed features. Finish a bounded item only when its remaining work is clear
and proportionate. The status and priority judgments below are provisional where
evidence is incomplete.

The current project goal is to help users find skills, understand their physical
installations and affected agents, diagnose installation/version/duplicate issues,
and apply understandable repairs through existing safe operations. Excellent
terminal UX is part of that goal. This ledger supersedes the former all-21
completion objective and older handoff instructions to finish every audit item.
The deferred table is context for future decisions, not a completion backlog.

## Snapshot and how to read it

- Inventory date: 2026-10-19. The original inspected checkout is
  `/home/kevin/code/gh-skillview`, branch `codex/standalone-unpin`, HEAD
  `c919199a6861b9aaf9301e31c4b2945e098da799`, which is older than
  `origin/main` at `bf3aaf05…`. Use `origin/main` tree references below for
  merged code that is absent from this checkout. PR #65's merged batch-15 work
  is on that line.
- PR #72, “Protect forced installs and honor custom directories,” merged as
  `65e07717086d1580c273ea81b83b1eb6b56521dd`, with reviewed head
  `4e77ee5d3c1732448fc1e0265156ba81baf71729`. Forced-install ownership
  protection and custom `--dir` behavior are essential product safeguards, not
  deferred work.
- PR #73, “Add selected-revision approvals and approved restore flows,” is open
  at `3fd29566cde0e9b8eeb2b48a0941f93defb28459`; the observed macOS/Windows
  tests, Windows metadata jobs, and four AOT jobs failed. This is advanced
  approval UX. Keep desired-pin protection essential, but defer the broader
  approval/restore feature pending a clear user need and a bounded repair plan.
- Release verification was not assessed in this inventory. “Merged into
  `origin/main`” does not claim a release build or release availability.
- Before the pause, a test-isolation fix for PR #73 was reviewed in
  `/home/kevin/code/gh-skillview-pr73-windows-policy-fix`: replacement fixtures
  receive private state roots, with an independent-writer regression. Validation
  and publication were still pending. This is a historical locator; verify its
  current contents before reuse. It does not resolve every failed CI job.
- `git worktree list --porcelain` showed the private worktrees named below on
  this host. Their existence and HEADs were observed; this does not assert that
  every worktree is clean, complete, or supported. Do not treat a private branch
  as merged product behavior. Historical `/tmp` receipts may disappear.
- The untracked `HANDOFF-*` files in the repository are user handoffs. Preserve
  them. They are historical records, not current release gates. The older
  `/home/kevin/temp/skillview-batch/HANDOFF-2026-10-16.md` was found, but the
  active `/tmp/skillview-maintenance-active-brief.md` was not found in the
  inspected locations.

“Implemented” below means code exists at the stated locator. “Verified” is
limited to the cited evidence. An implementation can remain useful while its
broader acceptance or portability proof is unfinished.

## Keep available: useful product work

| Feature and user benefit | Code/status | UX readiness and limits |
| --- | --- | --- |
| Search, preview, and install help users choose a skill before writing it. | Install/search screens and CLI dispatch are present in `src/SkillView.Core/Ui/InstallScreen.cs`, `InstallConfirmModal.cs`, and `src/SkillView.Core/Cli/CliDispatcher.cs`; merged into `origin/main`. | Existing TUI and CLI. Preserve the existing flow and catalog behavior. |
| Inventory, cleanup, Doctor, and the Changes queue help users understand installations and find actionable work. | `src/SkillView.Core/Ui/Tabs/InstalledTabView.cs`, `DoctorTabView.cs`, `ChangesTabView.cs`, `ChangesQueueBuilder.cs`, `MaintenanceQueueFormatter.cs`, `src/SkillView.Core/Inventory/`; merged into `origin/main`. | PR #75 added the Changes issue summary, recommendation, affected agents, expected change and undo guidance; F2 retains technical evidence. Interactive checks at 80×24, 100×26 and 120×36 are recorded in `agent_docs/evidence/pr75-ux-testing.md`. Update availability stays unknown until checked. The Cleanup follow-up below adds readable review summaries and has private Review → Apply → Rescan evidence at 80×24 and 120×36. |
| Compare, target one installation, and review an ordinary update or chosen Git tag before applying it. | `SkillComparisonService.cs`, `SavedUpdateCoordinator.cs`, `UpdatesTabView*.cs`, and `CliDispatcher.cs`; merged into `origin/main`, including tag selection in PR #78. | Existing CLI/TUI review flows. Reuse these bounded flows. Persistent revision approvals and approved-restore policy remain deferred. |
| Backup/history, restore, quarantine/undo, and local-change review preserve a recovery path. | `MaintenanceBackupStore.cs`, `MaintenanceBackupHistory.cs`, `QuarantineStore*.cs`, `ReplacementPlanStore.LocalReview.cs`, `ThreeWayPayloadReview.cs`; merged through PR #65 in `origin/main`. See the maintenance guides linked above. | Existing backend and CLI/TUI operations. Supported native metadata and owner coordination are used by these flows. Keep them available; broad recovery foundations are not unused merely because broader cases are deferred. |
| Basic pinning and protection against unintended replacement preserve desired revisions. | Install `--pin`, `ReplacementPinPolicy.cs`, and update review/CLI paths; merged into `origin/main`. | Existing CLI/UI pin creation and protection. Keep basic pin safety essential. |
| Standalone unpin removes a verified pin while retaining installed content and source identity. | `origin/main` includes `Maintenance/ReplacementPlanStore.Unpin.cs`, batch-16 final evidence, and the Updates review/apply path with `PinPolicy`; see `agent_docs/maintenance-standalone-unpin.md`. | Merged into `origin/main`. CLI and Updates review/apply flows exist. Keep available; schema 1–3 compatibility and exact pin-member rules remain part of its contract. Release availability was not assessed. |
| Local reimport and source switching can help recover a local source. | In `origin/main`: `Maintenance/ReplacementPlanStore.LocalReimport.cs`, `Maintenance/ReplacementPlanStore.LocalSourceSwitch.cs`, `Maintenance/ReplacementLocalSourceSwitch.cs`, `Ui/LocalReimportScreen.cs`, plus CLI dispatch. Private worktrees `codex/local-reimport` (`b5fff2c`) and `codex/local-source-switch` (`cd2928a`) also exist. | Merged code exists. Keep available; UX and end-to-end readiness were not assessed in this bounded pass. Do not infer remote adoption or resolution of moved/renamed/deprecated skills. |
| Effective-loading inspection provides a conditional local-policy view. | `EffectiveSkillLoader.cs`, `EffectiveLoadingScreen.cs`, and `agent_docs/maintenance-effective-loading.md`; merged into `origin/main`. | Existing UI/CLI plus provider-specific backend analysis. Keep source/version and unavailable reasons. No AI client is launched to populate it. |
| M19 evaluation work and its evidence remain available. | In the `origin/main` tree: `Evaluation/EvaluationReview.cs`, `Evaluation/EvaluationWorkspace.cs`, `IsolatedEvaluationRunner.cs`; CLI dispatch in `Cli/CliDispatcher.Authoring.cs`. Private `gh-skillview-isolated-evaluation` (`926c3f3`) is separate. | Keep available at low priority; it is not a release gate. CLI commands exist; runtime and interactive readiness were not reassessed. Do not remove completed evaluation work or expand the framework without a concrete need. |
| Static package checks help users find authoring compatibility issues before install. | In `origin/main`: `Authoring/AuthoringValidationService.cs`, `Cli/CliDispatcher.Authoring.cs`, `PackageChecking/StaticPackageChecker.cs`, `PackageCheckReport.cs`, `PackageCompatibilityChecker.cs`, `PackageDeclarationScanner.cs`, `MarkdownPackageReferences.cs`, and `Ui/PackageCheckScreen.cs`. | Existing backend/CLI/UI code. Readiness is based on source presence; no interactive TUI run was performed. Keep available. This static authoring check is distinct from a proposed broader skill lint queue. |
| Forced-install ownership checks and custom install directories preserve existing copies and honor the user's path choice. | Merged in PR #72 as `65e07717086d1580c273ea81b83b1eb6b56521dd`; [PR #72](https://github.com/harder/gh-skillview/pull/72). | Essential safeguards are now on `main`; the original CI and separate minimum/latest `gh` contract lanes passed on reviewed head `4e77ee5`. |

## Bounded work that may be worth finishing

| Feature and user benefit | Code/status | UX readiness and bounded next work |
| --- | --- | --- |
| Detaching one physical exposure while retaining other consumers may help split shared installs. | Private worktrees `gh-skillview-exposure-detach` (`d005eee`), `gh-skillview-exposure-detach-current-integration` (`fe5a5ed`), and selected integration (`e542431`); see handoff notes. Not merged. | UX readiness unknown. Handoff reports focused checks, while native positive evidence and full acceptance remain outstanding. First establish the smallest actual user gap; do not label it nearly complete. |

## Defer broad expansion

| Candidate | Where / status | UX readiness and reason to defer |
| --- | --- | --- |
| Generic transaction or migration framework expansion | Existing recovery/writer foundations in `src/SkillView.Core/Maintenance/`; private owner/registry and queue branches are in the worktree inventory. | Backend foundations already support current operations; no user-facing framework requirement established. Defer broadening; reconsider for one concrete repair with a defined recovery path. |
| Automatic consolidation of duplicate/shared skills | Private `gh-skillview-consolidation` (`980763f`) and proof/integration worktrees. | UX readiness unknown; candidate includes substantial proof work. Survivor and ownership decisions need explicit user review. Keep compare; reconsider only as a previewable, bounded action. |
| Machine profiles and broad profile management | Inventory observes configured profiles; private `gh-skillview-profile-*` worktrees. | Inventory UI/backend exists; profile lifecycle UX readiness unknown. Do not infer installed/enabled agents from shared placements. Defer absent a concrete user workflow. |
| Persistent selected-revision approval and approved-restore policy | PR #73 remains open at `3fd2956`; historical macOS/Windows tests, Windows metadata and AOT failures; private `codex/desired-revisions*` and `codex/selected-approved-*`. | Keep the merged one-time Git tag choice from PR #78. Defer the broader persistent approval/approved-restore policy until a concrete need and bounded recovery model are established. Preserve ordinary updates and basic desired-pin protection. |
| Additional native owner mutations | Private `gh-skillview-native-owner-*`; existing checked owner accessors and journals remain in use. | Existing backend supports current owner-aware operations. New mutation UX/readiness unknown. Defer additional surfaces until a specific repair requires them; retain existing safety/recovery code. |
| Broadening lint rules or turning package checks into a new general lint framework | In `origin/main`, `PackageChecking/StaticPackageChecker.cs`, `PackageCompatibilityChecker.cs`, `PackageDeclarationScanner.cs`, `MarkdownPackageReferences.cs`, `PackageCheckReport.cs`, `Ui/PackageCheckScreen.cs`, and `Maintenance/MaintenanceQueue.cs` already exist. | Keep current package checks and queue. Defer broad rule or framework expansion without a concrete user need and bounded criteria. `OpenCodePolicyShape.cs` is provider-specific policy shape, not portable skill lint. |
| Broad remote-source adoption | Private `gh-skillview-remote-source-adoption` (`59d360a`); historical handoff calls it unverified. | UX and source trust/recovery readiness unknown. Local reimport/source switching is narrower. Reconsider only with an explicit trust and recovery model. |
| Claude local plugin disable behavior | Private `gh-skillview-claude-plugin-disable` (`7ad1b5b`). | Historical case 1 was refused because the cache layout was unproven. Case 2 passed 23 pure tests and a C guard build with warnings treated as errors; the self-probe was not executed, and its prepared receipt needs a provenance correction. No actual disable proof is established. Revisit only with fresh layout/source evidence and a user need. |

## Next bounded priority

PR #72's install safeguards, PR #75's Changes summaries and PR #76's CI runtime
improvements are merged. PR #77's Cleanup review UX merged on 2026-10-20 as
`2a736402f6dfa9cbbdeda88c94940ff930691e03`. It replaces default technical
tables/IDs with a plain issue, action, affected-agent and recovery summary;
F2 keeps technical evidence available. Private keyboard walkthroughs at 80×24
and 120×36 verified Review → Apply → Rescan for a broken link and an empty
directory while retaining an unchanged healthy skill. Testing also exposed and
fixed empty directories being mislabeled as malformed skills; owner,
partial-coverage and symlink protections remain in force. Native validators
and removal authority are unchanged. A deterministic UI test verifies Esc drains
in-flight removal and retains partial-mutation counts. Managed/native CI passed
on the reviewed head. See `agent_docs/evidence/cleanup-review-ux.md` for validation
and its limits. Merged availability does not claim release availability.

The user authorized a bounded comparison of PR #73 with the existing update flow
on 2026-10-20. The result is [version-selection-scope.md](version-selection-scope.md).
It recommends choosing a Git tag for one installed copy through existing saved
update review/apply, with all shared consumers visible. It does not authorize
resuming PR #73 wholesale. The user subsequently approved the documented tag-only
implementation on 2026-10-20. Persistent approval and approved-restore policy,
raw commits and new pin behavior remain deferred. The bounded finish point is
the documented CLI/TUI flow, focused verification and a reviewable PR. That
implementation merged in PR #78 as `4276809d4e186b0501a2cd7fd6fbf746eb7a4c49`.
Managed tests, metadata checks, builds and CodeQL passed on its reviewed head;
native CI jobs encountered GitHub installation rate limits. The user explicitly
deferred the remaining native checks and merged it. This is not a completed
native-platform or release proof.

**Copies and versions** merged in [PR #79](https://github.com/harder/gh-skillview/pull/79)
as `96ddf90a329562f8c6adcbd4d4a200243511445d`. It shows related physical
installations and recorded consumers, compares two explicitly with the existing
read-only engine, and hands the selected exact location to existing update/version
or cleanup review. Local validation passed 54 focused and 9 integration tests,
with actual keyboard checks at 80×24. Validation limits are recorded in
[evidence/copies-comparison-ux.md](evidence/copies-comparison-ux.md).

The next bounded task, authorized after PR #79 merged, is **Updates review UX**
on `codex/update-review-ux`. The Copies walkthrough exposed crowded footer rows
and little room to read a selected installation at 80×24. Give the selected-copy
and saved-review views a full-width summary, show controls when applicable, and
keep existing technical evidence behind F2. Verify that independent source and
retention approvals, selected-tag targeting, local-change review and batch
continuation still work. Reuse the current saved updates and recovery machinery;
do not add schemas, persistent approvals or mutation operations. The bounded
finish is useful narrow-terminal review/apply navigation, focused verification
and a reviewable PR. Automatic consolidation, exposure detach and the broader
PR #73 policy remain deferred.

Implementation and local validation are recorded in
[evidence/update-review-ux.md](evidence/update-review-ux.md). The terminal Apply
walkthrough also exposed stale post-write version context; starting another
review now requires an explicit fresh inventory capture while preserving the
previous result for inspection. This uses existing inventory and saved-update
contracts rather than introducing another lifecycle.

## Maintenance guidance

Keep the inventory and removal safety boundaries in `AGENTS.md`; those are
current invariants, not aspirational backlog. Update this ledger when a deferred
candidate is reconsidered, recording its user benefit, exact code location,
verification, and decision. Do not turn the retired all-21 list back into a
release gate. Existing implementation history remains useful background; this
ledger records current prioritization and does not rewrite that history.
