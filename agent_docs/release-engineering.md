# Release engineering

- The release workflow lives in `.github/workflows/release.yml`.
- Publish Native AOT standalone artifacts for four RIDs on native runners: `win-x64`, `win-arm64`, `linux-x64`, `osx-arm64`.
- Release assets are the direct standalone binaries `skillview-<rid>[.exe]` plus per-platform `SHA256SUMS-<rid>.txt` checksum files.
- Releases are published directly with `softprops/action-gh-release`, modeled after the simpler `winget-tui-sharp` release flow.
- Linux AOT publish still needs `clang` and `zlib1g-dev`.
- Keep `workflow_dispatch` enabled so release packaging can be exercised without pushing a tag; only tag pushes publish a GitHub Release.
- `release.yml` now serializes publishes with a workflow-level concurrency lock.
- Each release build leg restores, builds in `Release`, and runs the full test suite before publishing AOT assets.
- Release artifact uploads keep 30-day retention, and a failed release opens or reuses an issue with the run link for follow-up.
- `.github/workflows/README.md` is the operator-facing overview for CI/release workflow behavior.
- `docs/runbooks/release-rollback.md` is the rollback procedure for live GitHub Releases and the current dark-launch package-manager jobs.
- Homebrew dark-launch scaffolding lives in `packaging/homebrew/skillview.rb.tmpl` and currently generates a formula artifact for the shipped Unix targets (`osx-arm64` and `linux-x64`) from stable-tag assets.
- WinGet dark-launch scaffolding lives in `packaging/winget/` and currently only generates manifest artifacts for package id `harder.SkillView`.
- Keep package-manager jobs gated behind repo variables (`HOMEBREW_TAP_ENABLED`, `HOMEBREW_TAP_REPO`, `WINGET_ENABLED`) until real publish automation is ready.
