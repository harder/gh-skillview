# AGENTS.md

## Repo

`gh-skillview` is a .NET 10 / Terminal.Gui v2 terminal UI and CLI for browsing,
installing, updating, and cleaning up `gh skill` skills.

## Why

This repo exists to make `gh skill` easier to inspect and safer to operate from
the terminal, with both a full-screen TUI and scriptable CLI commands.

## What

- `src/SkillView.Core/` — shared bootstrapping, CLI dispatch, `gh` adapters,
  inventory, logging, and all TUI screens.
- `src/SkillView.App/` — standalone `skillview` entrypoint.
- `src/SkillView.GhExtension/` — `gh skillview` extension entrypoint.
- `tests/SkillView.Tests/` — xUnit coverage for core services, CLI JSON output,
  and TUI helpers.
- `tests/SkillView.IntegrationTests/` — in-process Terminal.Gui integration
  smoke tests using the ANSI driver and one event-loop tick.

## How

- Build/style verification: `dotnet build`
- Full tests: `dotnet test --no-build`
- Integration tests only: `dotnet test tests/SkillView.IntegrationTests/SkillView.IntegrationTests.csproj`
- Launch TUI: `src/SkillView.App/bin/Debug/net10.0/osx-arm64/skillview`
- For CLI global flags such as `--scan-root`, pass them **before** the
  subcommand: `skillview --scan-root /tmp/root list --json`

## Critical agent notes

- Keep this file and `agent_docs/` up to date as new durable agent-facing
  workflow or testing lessons are discovered. Put short repo-wide rules here;
  put detailed procedures in a focused file under `agent_docs/`.
- Prefer the built `skillview` host binary over `dotnet run` for PTY-driven TUI
  automation. `dotnet run` can add first-run noise and long startup delays,
  especially with sandboxed `HOME`.
- For PTY-driven TUI testing, use an isolated temp workspace and verify
  side-effects from the shell after each destructive step. See
  `agent_docs/tui-pty-testing.md`.
- SkillView now requires `gh` 2.92.0 or newer, and capability probing includes
  preview support for `--allow-hidden-dirs`.
- When testing install flows against current `gh`, do not assume the UI's
  displayed agent labels match the `gh skill install --agent` accepted values.
  Re-check against `gh skill install --help`.
- Current package compatibility: SkillView is pinned to Terminal.Gui
  `2.1.0-rc.3`. `Microsoft.NET.Test.Sdk` `18.4.0` still breaks
  `TuiHelpersTests` with a `MemberNotNullWhenAttribute` `TypeLoadException`
  during Terminal.Gui config initialization, so keep the test SDK at `17.11.1`
  until that compatibility issue is resolved.
- `src/SkillView.Core/SkillView.Core.csproj` owns the default
  `TerminalGuiVersion` property. Keep the `PackageReference` on
  `Version="$(TerminalGuiVersion)"` so CI can override it via MSBuild without
  editing source.
- Terminal.Gui's modern lifecycle is now the right default for SkillView:
  use `Application.Create().Init()` to create the app instance and
  `IApplication.Dispose()` / `using` for teardown. Do not add new uses of the
  legacy static `Application.Init()` / `Application.Shutdown()` path.
- Tie async TUI work to the lifetime of the owning app/dialog with a
  `CancellationToken`, and only update UI through `app.Invoke()` while that
  lifetime is still active. Do not fall back to direct UI mutation after
  teardown.
- The main TUI host path now runs through `SkillViewApp.RunAsync(ct)`, and
  `EntryPoint.RunAsync` awaits it directly. Keep external cancellation wired to
  the app lifetime so Terminal.Gui can stop the active runnable via
  `IApplication.RunAsync(..., ct, ...)`.
- `SkillViewApp` now keeps the search shell and pane state, while
  `SkillViewWorkflowCoordinator` owns install/update/installed/remove/cleanup/
  doctor orchestration plus the shared inventory capture/rescan flow. Put new
  workflow-level behavior there unless it truly belongs to the search shell.
- Package-manager dark-launch scaffolding lives under `packaging/` and the
  release workflow only generates Homebrew / WinGet artifacts when the repo
  variables (`HOMEBREW_TAP_ENABLED`, `HOMEBREW_TAP_REPO`, `WINGET_ENABLED`) are
  explicitly enabled. It does not push to a tap repo or submit to WinGet yet.
- Terminal.Gui `2.1.0-rc.3` remains compatible with the modern
  `Application.Create().Init()` lifecycle; the local
  `UnconditionalSuppressMessage` workaround and temporary App-level warning mask
  stay removed after a verification publish proved the App entrypoint no longer
  needs them.
- Prefer `KeyBindings` for view-local command remaps like table preview
  shortcuts. Keep the current window/table `KeyDown` routing for app-level
  single-letter shortcuts because `TableView` still swallows unbound printable
  keys before they bubble.
- On Terminal.Gui `2.0.2-develop.57`, `TableView.CollectionNavigator = null`
  works again for disabling type-to-search. Prefer that documented path over
  the old custom matcher workaround.
- Sanitize untrusted text before assigning it to preview/detail/log panes.
  `TerminalEscapeSanitizer` is now the shared UI-layer guard for remote preview
  markdown, search metadata, installed-skill detail markdown, cleanup/remove
  summaries, and rendered log text.
- If Copilot-specific, Claude-specific, or other agent-platform guidance turns
  out to matter for this repo, capture the repo-relevant part here so future
  agents do not need to rediscover it from external docs.

## Progressive disclosure

- `agent_docs/running-tests.md` — standard verification commands, UI-focused
  test filters, and opt-in `gh` contract-test workflow details.
- `agent_docs/release-engineering.md` — release workflow, asset naming, AOT RID
  matrix, and attestation conventions.
- `docs/runbooks/release-rollback.md` — rollback steps for live GitHub Releases
  and the current Homebrew / WinGet dark-launch artifacts.
- `agent_docs/tui-pty-testing.md` — sandboxed PTY workflow, synchronization
  strategy, verification scripts, and known pitfalls for terminal UI testing.
