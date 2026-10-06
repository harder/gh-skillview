# GitHub automation

The repository keeps its workflows here and its committed NuGet lock files in
each project. `AGENTS.md` is the project-wide source of build and safety rules.

| Workflow | Trigger | Purpose |
| --- | --- | --- |
| `ci.yml` | `main`, PR, manual | Action lint, monitor tests, dependency review, site check, three-OS tests, and four-RID AOT smoke |
| `codeql.yml` | `main`, PR, weekly, manual | C# and GitHub Actions security analysis |
| `contract-tests.yml` | Daily, manual | Required live `gh` tests at the 2.97.0 minimum and latest release; open an assigned issue on failure |
| `critical-dependencies.yml` | Daily, manual | Check stable Terminal.Gui packages and latest GitHub CLI; open deduplicated, assigned review issues with release highlights, then add a bounded Copilot assessment |
| `pages.yml` | Site changes, manual | Build and deploy `site/` to GitHub Pages |
| `release.yml` | `v*` tag, manual | Build, attest, verify, and publish release assets; optionally generate package manifests |

Dependabot checks NuGet weekly, GitHub Actions weekly, and `global.json` monthly.
Terminal.Gui packages are grouped for joint review. The workflow Action pins are
full commit SHAs; the version comments show the release each SHA represents.
The dependency monitor's source and tests are in `.github/scripts/`.
For each newly opened issue, the monitor includes upstream release highlights,
directly relevant skill changes, and an explicit unverified compatibility status.
The Copilot CLI reads the issue, upstream notes, and SkillView code with read-only
tools. A separate job posts its assessment as a marked issue comment; the model
cannot write to GitHub. The workflow uses GitHub Actions' short-lived token with
`copilot-requests: write`, which bills a personal repository to its owner's
Copilot seat. A Copilot failure leaves the factual alert intact and fails the
workflow visibly. Manually dispatch with `issue_number` to assess an existing
`critical-dependency` issue. Reruns never create duplicate assessment comments.

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
to WinGet. The WinGet job verifies all Windows release checksums and renders a
copy-ready manifest tree; see `packaging/winget/README.md`. `HOMEBREW_TAP_REPO`
records the intended tap name.

Manual `workflow_dispatch` exercises release packaging without publishing a
GitHub Release. Keep the two product publishes sequential: they share core
intermediate files. Linux AOT requires `clang` and `zlib1g-dev`.

## Repository settings to keep enabled

- Dependabot alerts and security updates, the dependency graph, and secret
  scanning where available.
- GitHub Pages from Actions with custom domain `skillview.dev`.
- Release immutability once all assets and checks are validated.
- `ProtectMain` targets the default branch and requests Copilot review on new
  pull requests. As currently configured, it does not request another review
  on new pushes or drafts, and it does not require a pull request or passing
  checks before updating `main`. Enable those rules in repository settings if
  they should become merge requirements; workflow files alone cannot enforce
  them.

`agent_docs/release-engineering.md` contains release development notes.
