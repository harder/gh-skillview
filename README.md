# gh-skillview

`gh-skillview` is a friendly terminal app for exploring, installing, updating, and cleaning up AI agent skills built on top of `gh skill`.

It ships in two forms:

1. **GitHub CLI extension**: `gh skillview`
2. **Standalone binary**: `skillview`

SkillView gives you a fast Terminal UI for common workflows, plus scriptable CLI commands when you want JSON output or automation.

## Why use it?

SkillView is for people who want `gh skill` to feel easier to browse and safer to operate.

- Search and preview skills without leaving the terminal
- Install with guided options instead of memorizing flags
- Inspect installed skills across scan roots
- Run update dry-runs before changing anything
- Remove skills with safety checks
- Find cleanup candidates such as duplicates, broken symlinks, and orphaned files

## Requirements

### Runtime requirements

- **GitHub CLI**: `gh` **v2.92.0 or newer**
- A terminal that supports standard ANSI TUI behavior
- A working `gh` setup; `gh auth login` is recommended for the best experience

SkillView wraps `gh skill`, so `gh` is the main runtime dependency.

### Build requirements

- **.NET SDK**: `10.0.100` or newer in the same feature band
- On Linux AOT publish: `clang` and `zlib1g-dev`

### Notes

- Release binaries are **Native AOT** and **self-contained**
- You do **not** need a .NET runtime installed to use release artifacts

## Installation

### Install as a GitHub CLI extension

This is the primary install path.

```bash
gh extension install harder/gh-skillview
gh skillview
```

To upgrade later:

```bash
gh extension upgrade harder/gh-skillview
```

### Install as a standalone binary

Download the right asset from the
[latest release](https://github.com/harder/gh-skillview/releases) and place it on your `PATH`.

| Platform | Asset |
|---|---|
| Windows x64 | `skillview-win-x64.exe` |
| Windows ARM64 | `skillview-win-arm64.exe` |
| Linux x64 | `skillview-linux-x64` |
| Linux ARM64 | `skillview-linux-arm64` |
| macOS x64 | `skillview-osx-x64` |
| macOS ARM64 | `skillview-osx-arm64` |

## Quick start

### Launch the TUI

```bash
gh skillview
```

or, if you installed the standalone binary:

```bash
skillview
```

### First commands to try

```bash
skillview doctor
skillview search prompt
skillview list
skillview update --dry-run
skillview cleanup
```

### How the app behaves

- **No subcommand** launches the full-screen TUI
- **A subcommand** runs in CLI mode
- The binary name decides the invocation style:
  - `gh-skillview` -> GitHub CLI extension mode
  - `skillview` -> standalone mode

## Usage

### Common CLI commands

```bash
skillview                      # launch the TUI
skillview doctor               # environment, auth, and capability report
skillview doctor --json
skillview doctor --clear-logs

skillview list
skillview list --json
skillview list --scope=user
skillview list --agent=claude

skillview rescan

skillview search <query>
skillview search <query> --owner <owner> --limit 50 --page 2 --json

skillview preview OWNER/REPO [SKILL]
skillview preview OWNER/REPO@v1.2.3 [SKILL] --json

skillview install OWNER/REPO [SKILL] --agent claude --scope user
skillview install OWNER/REPO@v1.0.0 [SKILL] --pin --json

skillview update --dry-run
skillview update render-md fetch-url
skillview update --all --yes

skillview remove render-md
skillview remove render-md --yes

skillview cleanup
skillview cleanup --apply --yes
skillview cleanup --output cleanup-report.txt
```

### Global flags

```bash
skillview --debug
skillview --scan-root /path/to/skills
skillview list --json --debug
```

- `--debug` works **before or after** the subcommand
- `--scan-root` is repeatable
- `SKILLVIEW_LOG=debug` is also supported

### Exit codes

These are stable and safe to depend on in scripts.

| Code | Meaning |
|---|---|
| `0` | Success or nothing to do |
| `1` | User-level error |
| `2` | Invalid usage |
| `10` | Environment error |
| `20` | No matches |

## Keyboard reference

### Main TUI

| Key | Action |
|---|---|
| `/` | Focus the search box |
| `Enter` | Run search (query box) or preview (results) |
| `→` | Preview the selected result |
| `p` or `v` | Preview the selected result |
| `Esc` | Leave the query box and return focus to results |
| `l` or `r` | Toggle the right pane between preview and logs |
| `d` | Open Doctor |
| `I` | Open installed skills inventory |
| `s` | Open the full search dialog |
| `u` | Open update dialog |
| `c` | Open cleanup dialog |
| `F1` | Show help |
| `q` | Quit |

### Search dialog

| Key | Action |
|---|---|
| `Enter` | Search |
| `→`, `p`, or `v` | Preview selected result |
| `i` | Stage install for the selected result |
| `/` | Focus the query field |
| `Esc` or `q` | Close dialog |

### Installed dialog

| Key | Action |
|---|---|
| `/` | Return to the main Search view and focus the query box |
| `f` | Focus the Installed filter field |
| `o` | Open the selected installed skill folder |
| `s` | Change sort mode |
| `x` | Remove selected installed skill |
| `Esc` or `q` | Close dialog |

### Update dialog

| Key | Action |
|---|---|
| `Space` | Toggle the selected skill |
| `Dry-run` button | Preview updates without changing anything |
| `Update` button | Run update |
| `Esc` | Close dialog |

### Cleanup dialog

| Key | Action |
|---|---|
| `Space` | Toggle the selected cleanup candidate |
| `r` | Remove checked candidates |
| `i` | Mark checked candidates as ignored |
| `x` | Export cleanup report |
| `Esc` | Close dialog |

### Remove and install dialogs

- `Esc` closes the dialog
- Buttons are keyboard accessible through the normal Terminal.Gui focus model

## Logging

SkillView keeps a rotating file log and redacts sensitive material before writing.

- Linux: `~/.cache/SkillView/logs`
- macOS: `~/Library/Caches/SkillView/logs`
- Windows: `%LOCALAPPDATA%\\SkillView\\logs`

Behavior:

- daily rotation
- 14-day retention
- 50 MB total size budget
- GitHub tokens, `Authorization:` headers, and URL userinfo are redacted

## Architecture

SkillView is intentionally small and explicit.

### High-level design

- **3 production projects**
- **1 test project**
- shared logic lives in `SkillView.Core`
- both executables call the same entry point
- no DI container
- no reflection-heavy runtime magic

### Execution flow

```text
Program.cs (thin entrypoint)
  -> EntryPoint.RunAsync(args)
     -> ArgParser.Parse(...)
     -> TuiServices.Build(...)
     -> CLI mode: CliDispatcher.RunAsync(...)
     -> TUI mode: SkillViewApp.Run()
```

### Project structure

| Project | Purpose |
|---|---|
| `src/SkillView.Core` | Domain logic, adapters, inventory, logging, and Terminal UI |
| `src/SkillView.App` | Standalone `skillview` executable |
| `src/SkillView.GhExtension` | `gh-skillview` extension executable |
| `tests/SkillView.Tests` | xUnit tests |

### Core package layout

| Directory | Responsibility |
|---|---|
| `Bootstrapping` | Entry point, argument parsing, app options |
| `Cli` | Subcommand dispatcher and JSON/text rendering |
| `Environment` | `gh` discovery, version checks, auth and capability probing |
| `Gh` | Adapters for `gh skill` commands |
| `Gh/Models` | Records returned by adapters |
| `Inventory` | Filesystem scanning, parsing, reconciliation, cleanup classification |
| `Inventory/Models` | Installed skill and front-matter models |
| `Logging` | In-memory logger, redaction, file sink |
| `Subprocess` | Safe argv-array process execution |
| `Ui` | Terminal.Gui screens and helpers |

### Important architectural choices

#### Native AOT first

The app is designed around Native AOT constraints.

- no reflection-based discovery
- `System.Text.Json` source generators for JSON
- generated regexes instead of runtime regex compilation
- hand-rolled argument parsing
- hand-rolled front-matter parsing instead of a YAML dependency

#### Capability-gated `gh` integration

SkillView does not assume every `gh` build supports the same flags.

It probes `gh skill ... --help` so preview and install features can discover the flags the local installation actually supports, including shared flags like `--allow-hidden-dirs`. That keeps the app safer across evolving GitHub CLI releases.

#### Safe mutation operations

Install, update, remove, and cleanup flows all rescan inventory after changes. Removal logic resolves paths, checks root containment, and refuses dangerous operations by default.

## Repository layout

```text
.
├── src/
│   ├── SkillView.App/
│   ├── SkillView.Core/
│   └── SkillView.GhExtension/
├── tests/
│   └── SkillView.Tests/
├── script/
├── .github/
│   └── workflows/
├── Directory.Build.props
├── Directory.Build.targets
├── SkillView.sln
└── global.json
```

## Development guide

### Build and test

```bash
dotnet restore
dotnet build
dotnet test
```

There is no separate lint step. Build warnings are treated as errors through shared MSBuild settings.

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

Example for Linux x64:

```bash
dotnet publish src/SkillView.App -c Release -r linux-x64 \
  -p:PublishAot=true -p:StripSymbols=true -o dist/app
```

### Test philosophy

- xUnit for unit and behavior tests
- snapshot coverage for JSON-emitting CLI commands
- contract tests for `gh` integration
- release workflow verifies published artifacts

### Contributing notes

When changing behavior:

1. Prefer explicit, AOT-safe code over clever abstractions
2. Keep entrypoints tiny and business logic in `SkillView.Core`
3. Use `ProcessRunner` for subprocess work instead of shell composition
4. Preserve exit-code behavior for CLI consumers
5. Add or update tests with code changes

## Terminal compatibility

SkillView works best in terminals with full ANSI and xterm key support.

| Terminal | Status |
|---|---|
| Terminal.app (macOS) | ✅ Full support |
| iTerm2 | ✅ Full support |
| Kitty | ✅ Full support |
| Alacritty | ✅ Full support |
| Ghostty | ✅ Full support |
| Windows Terminal | ✅ Full support |
| Warp (macOS) | ⚠️ Partial — Enter key unreliable after first interaction; use `Ctrl+J` or `→` instead |

If you encounter issues in your terminal, try `--debug` and check the log output. See the [Logging](#logging) section for log file locations.

## Under the hood

SkillView is built with:

- [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) (v2) — the cross-platform .NET TUI framework that powers the full-screen interface
- [GitHub CLI](https://cli.github.com/) (`gh`) — all GitHub interaction flows through `gh skill` commands
- [.NET 10](https://dotnet.microsoft.com/) with Native AOT — single-binary, no runtime required
- [xUnit](https://xunit.net/) — test framework

## Acknowledgements

- The [GitHub CLI](https://cli.github.com/) team for the CLI, the extension framework, and the `gh skill` commands that make this project possible.
- The [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) maintainers ([@tig](https://github.com/tig), [@tznind](https://github.com/tznind), and contributors) for building a serious cross-platform TUI framework for .NET. The v2 rewrite is ambitious and impressive.
- The [GitHub Copilot](https://github.com/features/copilot) and [Claude Code](https://claude.com/product/claude-code) teams — this project uses AI-assisted development.

## License

MIT. See [LICENSE](./LICENSE).
