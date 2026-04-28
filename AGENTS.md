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
- `PHASE*_NOTES.md`, `implementation-plan.md` — design decisions and phase
  history.

## How

- Build/style verification: `dotnet build`
- Full tests: `dotnet test --no-build`
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
- Current package compatibility: Terminal.Gui `2.0.0-rc.6` is good, but
  `Microsoft.NET.Test.Sdk` `18.4.0` breaks `TuiHelpersTests` with a
  `MemberNotNullWhenAttribute` `TypeLoadException` during Terminal.Gui config
  initialization. Keep the test SDK at `17.11.1` until that compatibility issue
  is resolved.
- Record newly discovered PTY/session-specific bugs in
  `PTY_SESSION_UX_ISSUES.md`, not in the older `PHASE10_UX_ISSUES.md` backlog.
- If Copilot-specific, Claude-specific, or other agent-platform guidance turns
  out to matter for this repo, capture the repo-relevant part here so future
  agents do not need to rediscover it from external docs.

## Progressive disclosure

- `agent_docs/tui-pty-testing.md` — sandboxed PTY workflow, synchronization
  strategy, verification scripts, and known pitfalls for terminal UI testing.
