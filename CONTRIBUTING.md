# Contributing to SkillView

Thanks for helping improve SkillView. Report bugs and feature requests in [GitHub Issues](https://github.com/harder/gh-skillview/issues). For a bug, include the SkillView version, operating system and terminal, steps to reproduce, and a relevant debug log excerpt if available.

## Development

Build requirements:

- the .NET SDK selected by [`global.json`](global.json)
- on Linux AOT publish: `clang` and `zlib1g-dev`

### Architecture

SkillView uses explicit composition and shared services:

- **3 production projects**: `SkillView.Core`, `SkillView.App`, `SkillView.GhExtension`
- **2 test projects**: `SkillView.Tests`, `SkillView.IntegrationTests`
- shared logic lives in `SkillView.Core`
- both executables call the same entry point
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

The TUI has a persistent Discover, Installed, and Changes shell. `SkillViewApp`
owns that shell and its application lifetime; `SkillViewWorkflowCoordinator`
orchestrates install, update, inventory, remove, cleanup, and Doctor work.
Views under `src/SkillView.Core/Ui/Tabs/` render the primary and temporary
workspaces. Advanced install, remove, and cleanup flows use owned modals.
See the [architecture guide](docs/architecture.md) for the current boundaries.

SkillView requires GitHub CLI 2.97.0 or newer and checks `gh skill --help`
before enabling the app. When launched as an extension by GitHub CLI 2.101.0
or newer, it uses the host-provided `GH_PATH` for subprocesses.

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
dotnet restore --locked-mode
dotnet build
dotnet test --no-build
```

NuGet lock files are committed for all projects. After intentionally changing
package versions, run `dotnet restore --force-evaluate`, commit the lock-file
changes, and confirm `dotnet restore --locked-mode` still passes. See
[`agent_docs/running-tests.md`](agent_docs/running-tests.md) for filtered tests.

CI also verifies formatting with `dotnet format SkillView.sln --no-restore
--verify-no-changes`, lints GitHub Actions, and checks the static site.

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
