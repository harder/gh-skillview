# GitHub automation

The repository keeps its workflows here and its committed NuGet lock files in
each project. `AGENTS.md` is the project-wide source of build and safety rules.

| Workflow | Trigger | Purpose |
| --- | --- | --- |
| `ci.yml` | `main`, PR, manual | Action lint, monitor tests, dependency review, site check, three-OS tests, and four-RID AOT smoke |
| `codeql.yml` | `main`, PR, weekly, manual | C# and GitHub Actions security analysis |
| `contract-tests.yml` | Daily, manual | Required live `gh` tests at the 2.97.0 minimum and latest release; open an assigned issue on failure |
| `critical-dependencies.yml` | Daily, manual | Check stable Terminal.Gui packages and latest GitHub CLI; open deduplicated, assigned review issues |
| `pages.yml` | Site changes, manual | Build and deploy `site/` to GitHub Pages |
| `release.yml` | `v*` tag, manual | Build, attest, verify, and publish release assets; optionally generate package manifests |

Dependabot checks NuGet weekly, GitHub Actions weekly, and `global.json` monthly.
Terminal.Gui packages are grouped for joint review. The workflow Action pins are
full commit SHAs; the version comments show the release each SHA represents.
The dependency monitor's source and tests are in `.github/scripts/`.

## Release pipeline

`release.yml` validates a version tag and refuses to republish an existing
release. Four native jobs (`win-x64`, `win-arm64`, `linux-x64`, `osx-arm64`)
restore in locked mode, build and test, then publish standalone and `gh`
extension binaries sequentially. Each leg checks `--version`, stages two
binaries and a SHA-256 file, and attests the binaries.

The release job requires exactly 12 assets: eight binaries and four checksum
files. It checks every checksum before creating a draft release, checks the
published asset count, then makes the release public. Do not re-run a published
tag to replace binaries; cut a corrected tag. A failed job opens or reuses a
failure issue. See `docs/runbooks/release-rollback.md` for recovery.

Stable-tag Homebrew and WinGet jobs are opt-in via `HOMEBREW_TAP_ENABLED` and
`WINGET_ENABLED`. They download **published** release assets and generate
formula/manifests as workflow artifacts. They do not push to a tap or submit
to WinGet. `HOMEBREW_TAP_REPO` records the intended tap name.

Manual `workflow_dispatch` exercises release packaging without publishing a
GitHub Release. Keep the two product publishes sequential: they share core
intermediate files. Linux AOT requires `clang` and `zlib1g-dev`.

## Repository settings to keep enabled

- Dependabot alerts and security updates, the dependency graph, and secret
  scanning where available.
- GitHub Pages from Actions with custom domain `skillview.dev`.
- Release immutability once all assets and checks are validated.
- A `main` ruleset requiring PR review and passing CI checks; allow the
  maintainer to merge reviewed dependency PRs, not bots to bypass checks.

`agent_docs/release-engineering.md` contains release development notes.
