# gh-skillview

View and manage your AI agent skills in a beautiful Terminal UI. Available as
a GitHub CLI extension.

> [!NOTE]
> SkillView is under active development. The `main` branch is through
> Phase 7 of the plan in [`implementation-plan.md`](./implementation-plan.md):
> TG2 + .NET 10 Native AOT feasibility (Phase 0), environment probe +
> capability layer + file logging + Doctor (Phase 1), local inventory
> discovery with scan-root resolution, SKILL.md front-matter parsing,
> symlink / canonical-copy awareness, `.skillview-ignore` markers, the
> gated `gh skill list` adapter, and `LocalInventoryService` reconciliation
> (Phase 2), plus capability-gated `gh skill search` with owner/limit/page
> controls, versioned `gh skill preview` with associated-files extraction,
> a dedicated `SearchScreen` TUI with install-from-search staging, and
> `search` / `preview` CLI subcommands (Phase 3), plus the capability-gated
> `gh skill install` adapter, `InstallScreen` TUI dialog (agent multi-select
> checkboxes, scope `OptionSelector`, pin / force / upstream / repo-path /
> allow-hidden-dirs / from-local controls), search→install handoff, and the
> `install` CLI subcommand with pre/post inventory-diff rescan (Phase 4),
> plus the capability-gated `gh skill update` adapter with dry-run
> parsing, an `UpdateScreen` TUI (skill multi-select, pinned-skill flags,
> `--all` + `--yes` guardrails for the v2.91.0 interactive-prompt quirk),
> and the `update` CLI subcommand with a TreeSha-axis post-update inventory
> diff (Phase 5), plus the §12.1 safe-remove validator
> (realpath-resolved scan-root containment, ancestor-symlink-escape
> detection, `.git`-in-target guard, canonical-copy-with-incoming-symlinks
> second-confirm), per-file `RemoveService`, §12.2 cleanup classifier
> (malformed / orphan / duplicate / broken-symlink / hidden-nested /
> broken-shared-mapping / empty-directory), `RemoveScreen` + `CleanupScreen`
> TUIs (`x` from Installed, `c` from the shell), `.skillview-ignore`
> marker read/write, and the `remove` / `cleanup` CLI subcommands
> (dry-run-by-default remove, `cleanup --apply --yes`, exportable
> `--output` report, JSON output) (Phase 6), plus argv parser polish
> (`--debug` accepted anywhere, even after the subcommand), the
> documented exit-code contract in `skillview --help`, snapshot tests
> over every JSON-emitting subcommand (doctor / list / search /
> preview / install / update / remove / cleanup), and dispatcher-level
> argv-parser coverage for each subcommand (Phase 7).

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
skillview install OWNER/REPO [SKILL] [--agent claude] [--scope user]
skillview install OWNER/REPO@v1.0.0 --agent claude --agent cursor --pin --json
skillview update [SKILL]... [--all] [--dry-run] [--force] [--unpin] [--yes] [--json]
skillview update --dry-run                 # preview updates without mutating state
skillview update render-md fetch-url       # update named skills only
skillview remove render-md                 # dry-run safe removal (prints what would go)
skillview remove render-md --yes           # execute the removal after §12.1 checks
skillview cleanup                          # classify cleanup candidates
skillview cleanup --apply --yes            # apply: removes qualifying candidates
skillview cleanup --output report.txt      # write exportable cleanup report
skillview --debug              # Debug-level logging (flag beats SKILLVIEW_LOG env)
skillview --scan-root <path>   # add a custom scan root (repeatable)
skillview list --json --debug  # --debug is accepted anywhere on the command line
```

### Exit codes

Aligned with [cli/cli#13215](https://github.com/cli/cli/issues/13215) and
stable across releases — scripts and agent session hooks can depend on
these values:

| Code | Meaning |
|------|---------|
|  0   | Success / nothing to do |
|  1   | User-level error (input, conflict, refused destructive op) |
|  2   | Invalid usage (bad flags, missing args) |
| 10   | Environment error (gh missing, too old, no capability) |
| 20   | No matches |

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
