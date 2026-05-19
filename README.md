# gh-skillview

`gh-skillview` is a Terminal UI and CLI for discovering, previewing, installing, updating, removing, and cleaning up AI agent skills built on top of [`gh skill`](https://cli.github.com/manual/gh_skill).

It ships as both:

1. a GitHub CLI extension: `gh skillview`
2. a standalone binary: `skillview`

SkillView does **not** replace `gh skill`. It gives developers a faster full-screen workflow for the common cases, plus a scriptable CLI for inventory and maintenance tasks that are easier to reason about with SkillView's safety checks and JSON output.

![SkillView screenshot](./screenshot.png)

## Why this exists

`gh skill` is powerful, but once you start working with a lot of skills it helps to have:

- a tabbed workspace where Search, Installed, and Updates live side-by-side
- a persistent detail pane showing metadata and rendered `SKILL.md`
- guided install and update flows instead of memorizing flags
- a unified view of installed skills across project, user, and custom roots
- safe remove and cleanup workflows for duplicates, broken symlinks, residue, and malformed installs
- a CLI you can script without giving up the interactive TUI

## SkillView vs raw `gh skill`

SkillView complements `gh skill`; it does not try to replace every low-level command.

| If you need to... | Reach for... | Why |
|---|---|---|
| browse and compare skills quickly | **SkillView TUI** | tabbed Search/Installed/Updates layout with a persistent detail pane, batch updates, and staged install/remove flows |
| script inventory and maintenance | **SkillView CLI** | JSON output, stable exit codes, and remove/cleanup safety checks |
| experiment with an upstream flag the app does not surface yet | **raw `gh skill`** | direct access to the newest preview behavior without waiting for SkillView UI/CLI affordances |
| debug whether a feature is available in your installed GitHub CLI | **`skillview doctor`** | capability probing shows what the local `gh` actually supports |

## What SkillView wraps

SkillView builds on GitHub CLI's preview `gh skill` support. If you are new to the underlying commands, start with these docs:

- [`gh skill`](https://cli.github.com/manual/gh_skill)
- [`gh skill search`](https://cli.github.com/manual/gh_skill_search)
- [`gh skill preview`](https://cli.github.com/manual/gh_skill_preview)
- [`gh skill install`](https://cli.github.com/manual/gh_skill_install)
- [`gh skill update`](https://cli.github.com/manual/gh_skill_update)
- [Agent Skills specification](https://agentskills.io/specification)

## Requirements

- **GitHub CLI** `gh` **2.92.0 or newer**
- a working `gh` setup; `gh auth login` is recommended
- a terminal with normal ANSI TUI support; truecolor (24-bit) terminals get the full warm palette, others fall back to the nearest 256-color match

`gh skill` is still in preview and subject to change. SkillView probes the installed `gh` binary and only enables features whose flags are actually available.

## Install

### Install as a GitHub CLI extension

This is the primary install path.

```bash
gh extension install harder/gh-skillview
gh skillview
```

Upgrade later with:

```bash
gh extension upgrade harder/gh-skillview
```

### Install as a standalone binary

Download the right asset from the [latest release](https://github.com/harder/gh-skillview/releases), place it on your `PATH`, and run `skillview`.

| Platform | Asset |
|---|---|
| Windows x64 | `skillview-win-x64.exe` |
| Windows ARM64 | `skillview-win-arm64.exe` |
| Linux x64 | `skillview-linux-x64` |
| Linux ARM64 | `skillview-linux-arm64` |
| macOS x64 | `skillview-osx-x64` |
| macOS ARM64 | `skillview-osx-arm64` |

Release binaries are Native AOT and self-contained. You do not need a separate .NET runtime to use them.

Homebrew and WinGet scaffolding exists in the release workflow, but those channels are dark-launch only and are not public install paths yet.

## Quick start

Launch the TUI:

```bash
gh skillview
```

or:

```bash
skillview
```

A few good first commands:

```bash
skillview --help
skillview --version
skillview doctor
skillview search terraform
skillview list --json
skillview update --dry-run
skillview cleanup
```

## How to use SkillView

### TUI layout

The TUI is organized around three primary tabs in a persistent top header — **Search**, **Installed**, and **Updates** — plus a Doctor view reachable on demand. Each tab pairs a list on the left (60% of the width) with a contextual detail pane on the right (40%). The active tab is highlighted in the accent color; the status bar at the bottom advertises the shortcuts available in the current view.

| Tab / view | What it does | How to open |
|---|---|---|
| **Search** ◇ | Search public skills, refine by owner/agent/limit, preview `SKILL.md`, inspect metadata, flip the right pane between preview and logs, and stage installs. Default landing view. | `1`, click the pill, or `←/→` to cycle |
| **Installed** ▣ | Lists installed skills across discovered roots, filter, cycle sort, cycle a pinned/unpinned filter, inspect details, open the folder, or remove. | `2`, click the pill, or `←/→` |
| **Updates** △ | Mark skills with `Space`/`a` and batch-update with `U`, single-update the current row with `u`, or dry-run the whole inventory. Honors `--all`, `--force`, and `--unpin` when the local `gh` supports them. | `3`, `u`, click the pill, or `←/→` |
| **Doctor** | Full-screen environment report: `gh` path/version, auth state, detected capabilities, installed agent homes, and log location. Esc returns to the previous tab. | `d` |
| **Install — compact** | One-screen confirm: scope radio, agent checkboxes pre-selected from your home directory, **Install** / **Advanced…** / **Cancel**. | `i` from a Search result |
| **Install — advanced wizard** | Full multi-step dialog with version, scope, agent, path, overwrite, and capability-gated options (hidden-dir scanning, upstream, local installs). | `I` from a Search result, or **Advanced…** from the compact modal |
| **Remove — compact** | `[y]es / [n]o` confirm for simple single-skill removes. | `x` from an Installed row whose plan is straightforward |
| **Remove wizard** | Multi-step review/confirm for plans with incoming symlinks, validation warnings, or package/repo group removes. | Automatically escalated from `x` when needed |
| **Cleanup view** | Finds duplicates, broken symlinks, residue, and other cleanup candidates; remove or ignore them in batches. | `c` |
| **Help overlay** | Grouped Markdown reference for every keybinding. | `?` or `F1` |

Each tab preserves its own state (filter text, selection, sort, marks) when you switch away and back.

### Main view workflow

The main "discover and inspect" loop:

1. Type a search query, and optionally narrow the next search with the **Owner**, **Agent**, and **Limit** fields.
2. Browse results in the left table; selection drives the detail pane on the right.
3. Press `S` to cycle a sort (stars ↓ → name ↑ → name ↓ → repo ↑ → off). The active sort column's header shows the direction.
4. Press `e` to flip the detail pane between rendered markdown and raw, `o` to open the repo in a browser, or `l` to inspect logs.
5. Press `i` to stage an install, or `I` for the advanced wizard.

The same operations are available from the CLI.

### Keyboard reference

Navigation:

| Key | Action |
|---|---|
| `1` / `2` / `3` | Jump directly to Search / Installed / Updates |
| `←` / `→` | Cycle tabs |
| `↑` / `↓`, `PgUp` / `PgDn`, `Home` / `End` | Move through rows |
| `Tab` / `Shift+Tab` | Move focus between list and detail |
| `/` | Jump to Search and focus the search box |
| `?` or `F1` | Open the help overlay |
| `Esc` | Back out of the current sub-view / modal |
| `q` | Quit |

Search tab:

| Key | Action |
|---|---|
| `Enter` (or `Ctrl+J` in Warp) | Submit search from the query field, or preview from the results table |
| `p`, `v`, `→` | Preview the selected result |
| `S` | Cycle results sort |
| `i` | Compact install for the selected result |
| `I` | Advanced install wizard for the selected result |
| `o` | Open the repo in a browser |
| `e` | Toggle raw / rendered preview |
| `h` | Toggle hidden-dir access for preview/install |
| `l` | Toggle the right pane between preview and logs |

Installed tab:

| Key | Action |
|---|---|
| `f` | Focus the filter field |
| `s` | Cycle sort (name / package / scope) |
| `P` | Cycle pin filter (all / pinned only / unpinned only) |
| `x` | Remove the selected skill (compact confirm; wizard if the plan needs second-confirm) |
| `o` | Open the skill folder |

Updates tab:

| Key | Action |
|---|---|
| `Space` | Toggle the mark on the current row |
| `a` | Mark all visible rows |
| `A` | Clear all marks |
| `u` | Update the current row only |
| `U` | Update every marked row |
| `d` | Dry-run with the current selection |

Other:

| Key | Action |
|---|---|
| `d` | Open Doctor (full-screen) |
| `c` | Open Cleanup |

**Warp note:** if `Enter` is unreliable after the first interaction, use `Ctrl+J` or `→` for preview.

### Themes and configuration

- `--theme default` uses the SkillView warm palette (gold accent, beige text, dark surfaces, mint/red/blue/purple state colors) on truecolor terminals.
- `--theme high-contrast` switches to a 16-color StandardColor scheme for screen readers, terminals without truecolor, or low-contrast environments.
- `SKILLVIEW_THEME=high-contrast` is the environment-variable equivalent of `--theme high-contrast`.
- Keybindings are intentionally fixed in-app; there is no SkillView keybinding remap file, so the shortcuts documented here are the supported contract.

## CLI usage

SkillView runs in CLI mode when you provide a subcommand.

| Command | What it is for |
|---|---|
| `skillview doctor` | Inspect environment, auth, capabilities, and log paths |
| `skillview list` | Show installed skills from filesystem and, when available, `gh skill list` |
| `skillview rescan` | Re-run inventory capture and print a summary |
| `skillview search <query>` | Search public repositories for skills |
| `skillview preview OWNER/REPO [SKILL]` | Render a skill preview without installing |
| `skillview install OWNER/REPO [SKILL]` | Install a skill with SkillView's wrappers and diff output |
| `skillview update [...]` | Dry-run or apply skill updates |
| `skillview remove <skill>` | Remove an installed skill with safety checks |
| `skillview cleanup` | Report or apply cleanup actions |

Examples:

```bash
skillview list --json
skillview search prompt --owner github
skillview preview github/awesome-copilot documentation-writer
skillview install github/awesome-copilot git-commit --agent claude-code --scope user
skillview update --dry-run
skillview cleanup --apply --yes
```

### Global flags

```bash
skillview --help
skillview --version
gh skillview --help
gh skillview --version
skillview --debug
skillview --theme high-contrast
skillview --scan-root /path/to/skills
skillview --scan-root /path/one --scan-root /path/two list --json
```

- `--help` prints a Markdown usage guide for the active entrypoint (`skillview` or `gh skillview`)
- `--version` prints both the SkillView version and the Terminal.Gui version in use
- `--debug` works before or after the subcommand
- `--theme` accepts `default` or `high-contrast`
- `--scan-root` is repeatable
- `SKILLVIEW_LOG=debug` is also supported

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success or nothing to do |
| `1` | User-level error |
| `2` | Invalid usage |
| `10` | Environment error |
| `20` | No matches |

### Automation and AI-agent usage

SkillView's CLI is designed to be automation-friendly when you want higher-level safety than raw `gh skill`.

- Prefer `--json` on commands that support it: `doctor`, `list`, `search`, `preview`, `install`, `update`, `remove`, and `cleanup`.
- Use exit codes as the control surface for scripts: `0` success, `2` invalid usage, `10` environment/setup problems, `20` no matches.
- Put global flags like `--scan-root` and `--theme` **before** the subcommand; only `--debug` is accepted after the subcommand.
- `skillview doctor --json` is the fastest way for an agent or script to confirm `gh` version, auth state, capability probes, and log location before attempting an install/update flow.

Examples:

```bash
skillview doctor --json
skillview list --json
skillview search prompt --owner github --json
skillview update --all --dry-run --json
skillview cleanup --candidates --json
```

## Troubleshooting

SkillView keeps a rotating file log and redacts sensitive values before writing.

- Linux: `~/.cache/SkillView/logs`
- macOS: `~/Library/Caches/SkillView/logs`
- Windows: `%LOCALAPPDATA%\SkillView\logs`

If the TUI behaves unexpectedly:

1. run with `--debug`
2. open Doctor with `d`
3. check the log file

If you open a bug, include:

1. `skillview --version`
2. your terminal emulator and OS
3. the exact command or TUI flow
4. whether `gh auth status` is healthy
5. the relevant debug log excerpt if you have one

## For developers

Build requirements:

- .NET SDK `10.0.100` or newer in the same feature band
- on Linux AOT publish: `clang` and `zlib1g-dev`

### Architecture

SkillView is intentionally small and explicit:

- **3 production projects**: `SkillView.Core`, `SkillView.App`, `SkillView.GhExtension`
- **2 test projects**: `SkillView.Tests`, `SkillView.IntegrationTests`
- shared logic lives in `SkillView.Core`
- both executables call the same entry point
- no DI container
- Native AOT-safe code paths by default

Execution flow:

```text
Program.cs
  -> EntryPoint.RunAsync(args)
     -> ArgParser.Parse(...)
     -> TuiServices.Build(...)
     -> CLI: CliDispatcher.RunAsync(...)
     -> TUI: SkillViewApp.RunAsync(...)
```

The TUI is a single Terminal.Gui `Window` that hosts a persistent `TabBarView` header and four embedded view classes — `SearchTabView` logic lives inside `SkillViewApp` itself, `InstalledTabView` and `UpdatesTabView` in `src/SkillView.Core/Ui/Tabs/`, and `DoctorTabView` as a full-screen overlay. Tab activation flips `Visible` flags; no nested `Application.Run` subloops are used for the primary workflows. Escalation paths (advanced install wizard, remove wizard, cleanup) keep their modal `Application.Run` semantics intentionally.

### Project layout

| Path | Purpose |
|---|---|
| `src/SkillView.Core/` | Bootstrapping, CLI, `gh` adapters, inventory, logging, and Terminal.Gui views |
| `src/SkillView.Core/Ui/Tabs/` | `InstalledTabView`, `UpdatesTabView`, `DoctorTabView` |
| `src/SkillView.Core/Ui/Theming/` | Color palette + `ColorScheme` factories |
| `src/SkillView.App/` | Standalone `skillview` entrypoint |
| `src/SkillView.GhExtension/` | `gh skillview` extension entrypoint |
| `tests/SkillView.Tests/` | xUnit coverage for unit and screen-level tests |
| `tests/SkillView.IntegrationTests/` | In-process Terminal.Gui ANSI-driver smoke tests |
| `.github/workflows/` | CI, contract tests, and release workflows |

### Build and test

```bash
dotnet restore
dotnet build
dotnet test --no-build
```

The repo currently pins Terminal.Gui `2.2.1` and uses xUnit v3 test APIs
like `TestContext`; if you pulled package changes, run `dotnet restore` before
building so stale package assets do not leave the test projects on xUnit 2.x.

There is no separate lint step. Build warnings and code-style violations are treated as errors.

### Run locally

```bash
dotnet run --project src/SkillView.App --
dotnet run --project src/SkillView.App -- doctor
dotnet run --project src/SkillView.App -- search prompt
```

### Publish a local AOT build

```bash
dotnet publish src/SkillView.App -c Release -r osx-arm64 \
  -p:PublishAot=true -p:StripSymbols=true -o dist/app
```

On Linux, install `clang` and `zlib1g-dev` first.

## Built with

- [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) — the cross-platform .NET TUI framework
- [GitHub CLI](https://cli.github.com/) — all GitHub interaction flows through `gh skill` commands
- [.NET 10](https://dotnet.microsoft.com/) with Native AOT — single-binary, no runtime required
- [xUnit](https://xunit.net/)

## License

MIT. See [LICENSE](./LICENSE).
