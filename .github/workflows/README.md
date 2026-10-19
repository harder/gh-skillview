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
| `release.yml` | `v*` tag, manual | Build, attest, verify, and publish release assets; generate a stable-release Homebrew formula and optionally generate WinGet manifests |

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

CI's `.github/scripts/ci-change-scope.js` permits skipping managed/native proofs
only for its explicit user-documentation, screenshot/animation and static-site
allowlist. Workflow lint and site checks still run. All `agent_docs/` changes,
proof fixtures/scripts, dependencies, workflows and unknown paths require full
verification. The classifier compares exact local base/head commits, inspects
both rename paths and file modes, and defaults to full verification on missing,
empty, malformed or bounded diff evidence. Keep the allowlist narrow and run
`node --test .github/scripts/ci-change-scope.test.js` when changing it.

Native builds feed three independent proof groups per platform and reuse verified
historical producer binaries. The existing AOT check names still require every
group to pass. See [CI scope and native proof inputs](../../agent_docs/ci-runtime-optimization.md)
for cache, artifact and rerun contracts.

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

Every stable-tag release verifies published macOS ARM64 and Linux x64 assets
against their checksums and uploads a generated Homebrew formula. The separate
`harder/homebrew-tap` workflow checks for a new stable release hourly, opens
a formula PR, dispatches the tap's macOS and Linux validation, and merges only
the exact tested commit. Use its manual **Update SkillView formula** dispatch
to check immediately after a release. No cross-repository token is needed.
WinGet generation remains opt-in through `WINGET_ENABLED`; it verifies Windows
checksums and uploads a copy-ready manifest tree, but does not submit upstream.
See `packaging/winget/README.md`.

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
