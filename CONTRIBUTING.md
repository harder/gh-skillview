# Contributing to SkillView

Thanks for helping improve SkillView. Report bugs and feature requests in [GitHub Issues](https://github.com/harder/gh-skillview/issues). For a bug, include the SkillView version, operating system and terminal, steps to reproduce, and a relevant debug log excerpt if available.

## Development

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

The TUI is a single Terminal.Gui `Window` that hosts a persistent shell: `TabBarView`, `ContextBarView`, and `StatusStripView`. The primary workflows live in embedded `DiscoverTabView`, `InstalledTabView`, and `ChangesTabView` instances under `src/SkillView.Core/Ui/Tabs/`; `SkillViewApp` coordinates tab activation and shared shell state while those views own their workspace layouts. Temporary drill-in workspaces such as `UpdatesTabView` and `DoctorTabView` are still embedded views, but they are not part of the persistent three-tab shell. Tab activation flips `Visible` flags; no nested `Application.Run` subloops are used for the primary workflows. Escalation paths (advanced install wizard, remove wizard, cleanup) keep their modal `Application.Run` semantics intentionally.

SkillView uses GitHub CLI's preview `gh skill` commands. It checks for `gh` 2.97.0 or newer and runs `gh skill --help` before enabling the app. GitHub CLI 2.99.0 added support for Pi's `PI_CODING_AGENT_DIR` and corrected Codex's user-scope install location. When GitHub CLI 2.101.0 or newer starts SkillView as an extension, SkillView uses the host-provided `GH_PATH` so subprocesses run through that same CLI executable.

### Project layout

| Path | Purpose |
|---|---|
| `src/SkillView.Core/` | Bootstrapping, CLI, `gh` adapters, inventory, logging, and Terminal.Gui views |
| `src/SkillView.Core/Ui/Tabs/` | `DiscoverTabView`, `InstalledTabView`, `ChangesTabView`, plus temporary drill-in views like `UpdatesTabView` and `DoctorTabView` |
| `src/SkillView.Core/Ui/Theming/` | Color palette + `ColorScheme` factories |
| `src/SkillView.App/` | Standalone `skillview` entrypoint |
| `src/SkillView.GhExtension/` | `gh skillview` extension entrypoint |
| `tests/SkillView.Tests/` | xUnit coverage for unit and screen-level tests |
| `tests/SkillView.IntegrationTests/` | In-process Terminal.Gui ANSI-driver smoke tests |
| `.github/workflows/` | CI, contract tests, and release workflows |
| `site/` | Static product website source and dependency-free build script |

### Build and test

```bash
dotnet restore
dotnet build
dotnet test --no-build
```

The repo currently pins Terminal.Gui `2.5.0` (Terminal.Gui.Editor `2.5.7`) and
xUnit `4.0.1`. The release uses only stable package versions; the newer
Terminal.Gui development builds are intentionally not consumed. If you pulled
package changes, run `dotnet restore` before building so stale package assets
do not leave the test projects on xUnit 2.x.

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

### Website

The product website lives in `site/`. Its build script copies selected media
from `assets/` into an ignored `dist/skillview-site/` output folder, so the
repository keeps one source copy of each screenshot and GIF.

```bash
python site/build.py
python -m http.server 8000 --directory dist/skillview-site
```

Open `http://localhost:8000` to preview it. The Pages workflow builds and
publishes this output on site or media changes to `main`. The custom domain is
configured in the repository's GitHub Pages settings.
