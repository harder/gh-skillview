# Repository automation contracts

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

- `.github/dependabot.yml` scans NuGet projects, GitHub Actions, and the .NET
  SDK in `global.json`. It groups the two Terminal.Gui packages for joint
  review, leaves unrelated third-party Actions separate, and assigns update
  PRs to the maintainer. Verify a Dependabot PR with locked restore, tests,
  and affected AOT hosts before merging.
- `.github/workflows/critical-dependencies.yml` checks stable NuGet releases
  of Terminal.Gui and Terminal.Gui.Editor and the latest GitHub CLI release.
  Its tested script opens deduplicated, assigned compatibility issues with
  release highlights and concrete checks. A read-only Copilot CLI job adds a
  separately labeled assessment through a publisher job; this is provisional
  evidence, not a compatibility verdict. Manual dispatch accepts an existing
  labeled issue number for reassessment. It does not change the `gh` minimum or
  merge package PRs automatically. Update its parsers/tests if version storage
  changes.
- `ci.yml` checks Actions syntax, the dependency monitor, the static site,
  dependency diffs, .NET tests on Linux/macOS/Windows, and four native AOT
  targets. `codeql.yml` analyzes C# and Actions. `contract-tests.yml` tests
  both the minimum supported `gh` and the latest `gh`; both lanes are required
  and failures create a follow-up issue.
  Contract authentication checks the required repository API with the supplied
  Actions token. A `/user` denial does not establish that `gh auth status` fails
  or that the token cannot read repository data. Keep required tests and minimum
  permissions; retain response headers to distinguish access from quota errors.
- `release.yml` uses locked restore, tests, AOT publishes, version and checksum
  checks, provenance attestations, a complete-asset check, and a draft release
  before publication. Published tag reruns are refused. Stable releases also
  generate a verified Homebrew formula artifact; the tap polls releases and
  validates formula PRs on macOS and Linux before merging. WinGet generation
  remains opt-in.
- Workflow Actions are pinned to full commit SHAs. Update the SHA and version
  comment together after checking the upstream release. Keep permissions at
  workflow or job minimums; issue writers need `issues: write`, releases need
  `contents: write`, and provenance needs `id-token: write` and
  `attestations: write`.
- `.github/copilot-instructions.md` links here and contains only advice for
  Copilot review/coding automation. Keep general project facts and durable
  invariants in this file or focused `agent_docs/` guides.

- Keep this file and `agent_docs/` up to date as new durable agent-facing
  workflow or testing lessons are discovered. Put short repo-wide rules here;
  put detailed procedures in a focused file under `agent_docs/`.

- Documentation screenshots and GIFs live under `assets/screenshots/` and
  `assets/animations/`. Use relative links from Markdown; keep the README
  selective and put detailed user guidance in `docs/usage.md` and developer
  guidance in `CONTRIBUTING.md`.

- The stable release workflow always generates a Homebrew formula artifact from
  verified published binaries using `packaging/homebrew/render_formula.py`.
  The separate `harder/homebrew-tap` repository checks the latest stable
  release hourly, opens a formula PR, dispatches validation on macOS ARM64 and
  Linux x64, and merges only the exact validated commit. It needs no
  cross-repository secret. WinGet generation remains gated by `WINGET_ENABLED`
  and does not submit to `microsoft/winget-pkgs`.
  `packaging/winget/New-WinGetManifest.ps1` verifies published Windows assets
  against their checksums and renders copy-ready multi-file manifests. Keep
  the standalone WinGet package portable, its command alias `skillview`, and
  its `GitHub.cli >= 2.97.0` dependency in sync with the product contract.

- If Copilot-specific, Claude-specific, or other agent-platform guidance turns
  out to matter for this repo, capture the repo-relevant part here so future
  agents do not need to rediscover it from external docs.
