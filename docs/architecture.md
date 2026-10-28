# Architecture

SkillView ships two executables: the standalone `skillview` app and the
`gh skillview` extension. Both call `EntryPoint.RunAsync` in `SkillView.Core`,
so CLI behavior and the terminal interface share the same services.

## Entry and command paths

- `src/SkillView.App/` and `src/SkillView.GhExtension/` contain the entrypoints.
- `src/SkillView.Core/Bootstrapping/` parses global arguments and creates
  application services. `GhBinaryLocator` resolves `gh` and requires version
  2.97.0 or newer. The cached local `EnvironmentProbe` checks `gh skill --help`;
  startup does not probe remote authentication. Doctor checks auth on demand;
  an explicit Discover search loads optional account context independently.
  SkillView does not probe individual flags at runtime.
- `src/SkillView.Core/Cli/` dispatches commands and formats text or JSON output.
  The small command surface uses explicit parsers so it stays Native AOT safe.
- `src/SkillView.Core/Gh/` invokes `gh` with argument arrays. Install and
  listing calls put trusted flags before `--` and selectors after it.

Saved replacements use purpose-specific CLI commands and the shared saved-operation
coordinators/store. The retired generic mutation facade creates no new plan
references. A bounded `inspect --plan-file` reader supplies unverified specialist
recovery guidance for existing references; it cannot execute an operation. Native
plan schemas, hashes and retained originals remain the recovery authority.

## Terminal interface

`SkillViewApp` owns the Terminal.Gui application lifetime, the persistent
Discover/Installed/Issues shell, and shared pane state.
`SkillViewWorkflowCoordinator` orchestrates install, update, inventory, remove,
cleanup, and Doctor workflows. The views in `src/SkillView.Core/Ui/Tabs/`
render those workflows. Dialogs and background tasks retain explicit lifetime
and cancellation owners; UI updates run through the active application loop.
Selection requests retain generation, context and exact-object checks through
queued dispatch. Leaving a view cancels its unstarted work; a started operation
keeps its owned lease until its awaited completion and reconciliation settle.

F5 Locations composes saved TUI choices with per-category explicit CLI overrides.
Inventory captures and operations use the current captured discovery options,
not a later dialog draft. F4 History inspects saved replacements and offers
separately approved Abandon or Discard Undo data only after fresh retirement
inspection; it does not resume work automatically.

Issues uses typed captured findings for family membership. Registry-only rows
have no installation subject. Family expansion and bounded inline comparisons
are read-only; a family hand-off requires an exact child. Ordinary exact rows
retain their own review routes. Guided
review freezes eligible exact items and reconciles each before the next review.
Preferences, availability and comparison results grant no mutation authority.
See the [supported actions matrix](supported-actions.md).

## Inventory and removal

The inventory combines local skill directories with `gh skill list` data.
Physical copies retain every observed consumer, owner conflict and exposure.
Missing roots, partial coverage and unknown sources remain explicit unknowns;
equal names or normalized file content do not prove redundancy or non-use.
`PathIdentity` supplies path equality and containment. Real removal uses the
platform-specific `SecureRemovalBackend` after validation has captured the
target's native identity and its allowed scan root. It never falls back to
recursive path deletion when the secure backend is unavailable. Saved updates
prepare one immutable candidate and separately review source and retention.
Replacement retains the original local edits and extras for fresh Undo; it does
not merge them into the candidate. Standalone quarantine uses its own native
rename, journal and Undo review, separately from saved replacement History. See
[`AGENTS.md`](../AGENTS.md) and
[`agent_docs/ui-lifecycle-and-resource-bounds.md`](../agent_docs/ui-lifecycle-and-resource-bounds.md)
for the detailed safety contracts.

## Builds and releases

The solution targets .NET 10 and Terminal.Gui 2.5.0. Dependency lock files are
committed; CI restores in locked mode, tests on Linux, macOS, and Windows, and
smoke-publishes four Native AOT targets. Release tags trigger the asset,
checksum, provenance, and publication workflow described in
[`agent_docs/release-engineering.md`](../agent_docs/release-engineering.md).

New local merge creation and its editor are retired. `ReplacementPlanStore`
retains schema-3 DTOs, readers, choice/resolution validation and native metadata
checks for saved-plan inspection, admitted apply, recovery and undo. These share
metadata/archive primitives with unpin and local reimport; they are not dead
creator code. Ordinary update comparison, keep-local and reviewed replacement
continue through the saved-update workflow.
