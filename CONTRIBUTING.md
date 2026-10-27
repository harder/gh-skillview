# Contributing to SkillView

Thanks for helping improve SkillView. Report bugs and feature requests in [GitHub Issues](https://github.com/harder/gh-skillview/issues). For a bug, include the SkillView version, operating system and terminal, steps to reproduce, and a relevant debug log excerpt if available.

## Development

For the opt-in targeted resource replacement journey, prepare official checksummed
gh hosts with `tests/native/prepare-gh-proof-tool.py`, then run
`TMPDIR=/var/tmp python3 tests/native/prove-reviewed-update-tui.py --app <built-skillview> --gh <verified-gh> --resource-replacement --columns 120 --rows 36 --output <receipt.json>`.
Repeat at `--columns 80 --rows 24`. This uses actual network requests and native
apply/Undo in a retained private HOME/state/CWD; skill code is never executed.
Delegated gh authentication and unchanged native admission are required.
The hidden cli/cli `.github/skills/cli-exercise` fixture installs unpinned with
`--allow-hidden-dirs`. Its recorded `refs/tags/` prefix is removed once for the
existing tag choice, with ref/tree asserted before and after preparation. A
different frozen subtree stops the proof. Package B uses gh 2.97.0 and 2.103.2
for local evidence only; CI's 2.102.0 lane is unchanged. Machine receipts do not
replace the human release walkthrough.

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
Run scoped formatting from the repository root with repository-relative
`--include` paths. The pinned SDK 10.0.113 formatter can select zero files for
absolute-only paths and exit 0; confirm selection using reports or diagnostics.
Always run the full solution formatting check above before finishing. See
[scoped formatting](agent_docs/running-tests.md#scoped-formatting) for details.

### Offline inventory measurements

Use Python 3 and a built current host; the PTY observer supports Linux/macOS.
Keep the SDK/runtime pins and lock files unchanged. Run sequentially:

```bash
dotnet restore --locked-mode
dotnet build src/SkillView.App/SkillView.App.csproj -c Release --no-restore
python3 tools/measure_inventory.py \
  --app src/SkillView.App/bin/Release/net10.0/linux-x64/skillview \
  --expect-head "$(git rev-parse HEAD)" --samples 5
```

Adjust the built host RID/path for your machine. The default creates and prints
a private results location under `/var/tmp` on Linux; `--output` must name a new
directory outside checkout/ancestor `.git` markers. Read `report.json`, the CLI
JSON/stderr, and PTY ANSI/screen/log files. Results identify dirty source and
exact binaries; they do not certify release budgets. No credentials or network
cohort is required: local gh list is real, auth status is an explicit offline stub.
For a CLI-only timing correction, add `--cli-only` and retain any earlier PTY
receipt separately with its original hash. Schema-2 CLI receipts include direct
host start/exit-observation counters and hashes for the raw sample artifacts;
messy/large CLI timings each represent one observation, not a distribution.

Generate just the reusable story or workload without launching any processes:

```bash
python3 tools/inventory_fixture.py --kind messy
python3 tools/inventory_fixture.py --kind performance --copies 200 --roots 50
python3 -m unittest discover -s tools/tests -p 'test_*.py'
```

Each generated root retains `fixture.json`, `environment.json`, and a relative
content/mode manifest. The harness verifies CLI evidence for the messy story,
complete workload coverage/counts, and unchanged fixture input identities/bytes/
modes. Unsupported links or permission denial are labelled explicitly. Keep the
private mode-000 location intact while inspecting coverage; permission-bypassing
identities do not prove inaccessible discovery.

In a clean dedicated checkout, the existing opt-in `tools/test_once.py` also
accepts `--inventory-performance` alongside its normal `--expect-commit` and
`--output` arguments. That broader runner still performs its existing proofs;
use the standalone measurement command for this read-only slice alone. See the
[method and limits](agent_docs/inventory-performance.md#disposable-workloads-and-opt-in-measurements)
and [Linux baseline](agent_docs/inventory-performance-linux-baseline.md).

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

Saved schema-3 merge lifecycle proofs use the frozen historical producer, since
new merge creation is retired. Pass `--old-app` with `--old-checkout` (or a
verified `--old-source-receipt`) to `prove-local-merge-update.py` and the shared
revision proof's `--merged` mode. The disk-full Docker wrapper takes the same
producer through `--legacy-app` and `--legacy-checkout`. Current binaries perform
apply/recovery/undo. See [saved disposition compatibility](agent_docs/maintenance-local-dispositions.md).

Merge lifecycle fixtures use the source-reviewed last creator at `27efe94`,
while `prove-old-native-plans.py` keeps the separate `0dba59be` pre-source-review
shape. CI binds both exact producers through the existing native-input receipts;
reviewed merge proofs require source acknowledgement before any current apply.

The read-only retained-operation History proof needs no gh authentication or
network. After building, run the native proof host and TUI together through:

```sh
TMPDIR=/var/tmp python3 tests/native/prove-retained-history.py \
  --app src/SkillView.App/bin/Debug/net10.0/linux-x64/skillview \
  --proof tests/SkillView.MaintenanceProof/bin/Debug/net10.0/SkillView.MaintenanceProof \
  --output /tmp/skillview-history-proof
```

Use the corresponding built host paths on other native platforms. The tool
creates fresh marked private fixtures, retains them for review, and verifies
80×24/120×36 keyboard browsing, restart hints, explicit Inspect and unchanged
payload/state bytes, modes and native identities. It is a local regression proof,
not new human release acceptance. Avoid `/tmp` fixture ancestry in sandboxes
that mount a Git marker there; keep the native placement guard intact.
