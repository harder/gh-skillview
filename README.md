# gh-skillview

View and manage your AI agent skills in a beautiful Terminal UI. Available as
a GitHub CLI extension.

> [!NOTE]
> SkillView is under active development. The `main` branch is through
> Phase 3 of the plan in [`implementation-plan.md`](./implementation-plan.md):
> TG2 + .NET 10 Native AOT feasibility (Phase 0), environment probe +
> capability layer + file logging + Doctor (Phase 1), local inventory
> discovery with scan-root resolution, SKILL.md front-matter parsing,
> symlink / canonical-copy awareness, `.skillview-ignore` markers, the
> gated `gh skill list` adapter, and `LocalInventoryService` reconciliation
> (Phase 2), plus capability-gated `gh skill search` with owner/limit/page
> controls, versioned `gh skill preview` with associated-files extraction,
> a dedicated `SearchScreen` TUI with install-from-search staging, and
> `search` / `preview` CLI subcommands (Phase 3).

## Installation

### As a `gh` extension (primary)

```bash
gh extension install harder/gh-skillview
gh skillview
```

### As a standalone binary

Download the appropriate `skillview-<RID>[.exe]` asset from the
[latest release](https://github.com/harder/gh-skillview/releases) and place
it on your `PATH`.

## Requirements

- `gh` CLI **v2.91.0 or newer** (earlier versions lack the `gh skill`
  subcommand set SkillView depends on).
- No .NET SDK or runtime required on the target machine — binaries are
  Native AOT-compiled and self-contained.

## Usage

```
skillview                      # launch the TUI
skillview doctor               # environment + gh capability report
skillview doctor --json        # machine-readable doctor output
skillview doctor --clear-logs  # wipe rotated log files
skillview list                 # installed-skill inventory (table)
skillview list --json          # machine-readable inventory snapshot
skillview list --scope=user    # filter by scope (project|user|custom)
skillview list --agent=claude  # filter by agent id
skillview rescan               # capture a fresh inventory snapshot
skillview search <query>       # gh skill search adapter
skillview search <q> --owner o --limit 50 --json
skillview preview OWNER/REPO [SKILL] [--version <ref>] [--json]
skillview preview OWNER/REPO@v2.0.0 SKILL  # versioned preview shorthand
skillview --debug              # Debug-level logging (flag beats SKILLVIEW_LOG env)
skillview --scan-root <path>   # add a custom scan root (repeatable)
```

Logs are written daily-rotated under the platform cache directory
(`~/.cache/SkillView/logs` on Linux, `~/Library/Caches/SkillView/logs` on
macOS, `%LOCALAPPDATA%\SkillView\logs` on Windows), mode `0600` on POSIX,
retained 14 days with a 50 MB total-size budget. GitHub tokens,
`Authorization:` headers, and URL userinfo are redacted at the log-writer
layer.

## Layout

```
src/
  SkillView.Core/          all domain logic, services, and TG2 UI
  SkillView.App/           skillview standalone entrypoint
  SkillView.GhExtension/   gh-skillview extension entrypoint
tests/
  SkillView.Tests/         xunit
.github/workflows/
  ci.yml                   build + test + AOT-publish smoke
  release.yml              six-RID AOT matrix + SLSA attestations
script/
  build-noop.sh            stub for cli/gh-extension-precompile@v2
```

## Development

```bash
dotnet restore
dotnet build
dotnet test
# AOT publish (Linux requires clang and zlib1g-dev):
dotnet publish src/SkillView.App -c Release -r linux-x64 \
  -p:PublishAot=true -p:StripSymbols=true -o dist/app
```

## License

MIT — see [`LICENSE`](./LICENSE).
