# Architecture

SkillView ships two executables: the standalone `skillview` app and the
`gh skillview` extension. Both call `EntryPoint.RunAsync` in `SkillView.Core`,
so CLI behavior and the terminal interface share the same services.

## Entry and command paths

- `src/SkillView.App/` and `src/SkillView.GhExtension/` contain the entrypoints.
- `src/SkillView.Core/Bootstrapping/` parses global arguments and creates
  application services. `GhBinaryLocator` resolves `gh` and requires version
  2.97.0 or newer. `EnvironmentProbe` checks `gh skill --help` once; SkillView
  does not probe individual flags at runtime.
- `src/SkillView.Core/Cli/` dispatches commands and formats text or JSON output.
  The small command surface uses explicit parsers so it stays Native AOT safe.
- `src/SkillView.Core/Gh/` invokes `gh` with argument arrays. Install and
  listing calls put trusted flags before `--` and selectors after it.

## Terminal interface

`SkillViewApp` owns the Terminal.Gui application lifetime, the persistent
Discover/Installed/Changes shell, and shared pane state.
`SkillViewWorkflowCoordinator` orchestrates install, update, inventory, remove,
cleanup, and Doctor workflows. The views in `src/SkillView.Core/Ui/Tabs/`
render those workflows. Dialogs and background tasks retain explicit lifetime
and cancellation owners; UI updates run through the active application loop.

## Inventory and removal

The inventory combines local skill directories with `gh skill list` data.
`PathIdentity` supplies path equality and containment. Real removal uses the
platform-specific `SecureRemovalBackend` after validation has captured the
target's native identity and its allowed scan root. It never falls back to
recursive path deletion when the secure backend is unavailable. See
[`AGENTS.md`](../AGENTS.md) and
[`agent_docs/ui-lifecycle-and-resource-bounds.md`](../agent_docs/ui-lifecycle-and-resource-bounds.md)
for the detailed safety contracts.

## Builds and releases

The solution targets .NET 10 and Terminal.Gui 2.5.0. Dependency lock files are
committed; CI restores in locked mode, tests on Linux, macOS, and Windows, and
smoke-publishes four Native AOT targets. Release tags trigger the asset,
checksum, provenance, and publication workflow described in
[`agent_docs/release-engineering.md`](../agent_docs/release-engineering.md).
