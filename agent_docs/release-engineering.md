# Release engineering

- The release workflow lives in `.github/workflows/release.yml`.
- Publish Native AOT artifacts for six RIDs on native runners: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.
- Release assets come in two families:
  - GitHub CLI extension binaries use Go-style names for `gh extension install`: `gh-skillview-<go-os-arch>[.exe]`
  - Standalone binaries use .NET RID names: `skillview-<rid>[.exe]`
- `cli/gh-extension-precompile@v2` is the release publisher and attestation source. Keep `script/build-noop.sh` as the no-op build override for that action.
- Linux AOT publish still needs `clang` and `zlib1g-dev`.
- Keep `workflow_dispatch` enabled so release packaging can be exercised without pushing a tag.
- `release.yml` now serializes publishes with a workflow-level concurrency lock.
- Each release build leg restores, builds in `Release`, and runs the full test suite before publishing AOT assets.
- Release artifact uploads keep 30-day retention, and a failed release opens or reuses an issue with the run link for follow-up.
- `.github/workflows/README.md` is the operator-facing overview for CI/release workflow behavior.
- `docs/runbooks/release-rollback.md` is the rollback procedure for live GitHub Releases and the current dark-launch package-manager jobs.
- Homebrew dark-launch scaffolding lives in `packaging/homebrew/skillview.rb.tmpl` and currently only generates a formula artifact from stable-tag assets.
- WinGet dark-launch scaffolding lives in `packaging/winget/` and currently only generates manifest artifacts for package id `harder.SkillView`.
- Keep package-manager jobs gated behind repo variables (`HOMEBREW_TAP_ENABLED`, `HOMEBREW_TAP_REPO`, `WINGET_ENABLED`) until real publish automation is ready.
