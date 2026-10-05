# Release engineering

- The release workflow lives in `.github/workflows/release.yml`.
- Publish Native AOT standalone artifacts *and* `gh` extension binaries for four RIDs on native runners: `win-x64`, `win-arm64`, `linux-x64`, `osx-arm64`.
- Release assets use Go's OS/arch vocabulary (`windows`/`linux`/`darwin`, `amd64`/`arm64`) for the asset suffix — required for `gh extension install` to auto-detect a precompiled binary, and kept consistent across every asset to reduce confusion. Standalone binaries: `skillview-windows-amd64.exe`, `skillview-windows-arm64.exe`, `skillview-linux-amd64`, `skillview-darwin-arm64`. `gh` extension binaries (built from `src/SkillView.GhExtension`, required naming `gh-<extension-name>-<os>-<arch>[.exe]`): `gh-skillview-windows-amd64.exe`, `gh-skillview-windows-arm64.exe`, `gh-skillview-linux-amd64`, `gh-skillview-darwin-arm64`. Each platform also gets one `SHA256SUMS-<platform>.txt` covering both its binaries.
- NuGet dependencies are restored from committed lock files in locked mode.
  Update lock files deliberately before cutting a tag.
- A tag preflight validates the version format and rejects a tag whose release
  already exists. The release job requires all eight binaries and four checksum
  files, verifies checksums, creates a draft with `softprops/action-gh-release`,
  confirms its asset count, and only then publishes it. Do not overwrite
  published release bytes; publish a corrected version from a new tag.
- Linux AOT publish still needs `clang` and `zlib1g-dev`.
- Keep `workflow_dispatch` enabled so release packaging can be exercised without pushing a tag; only tag pushes publish a GitHub Release.
- `release.yml` now serializes publishes with a workflow-level concurrency lock.
- Each release build leg restores, builds in `Release`, runs the full test suite,
  publishes both AOT hosts sequentially, and checks each binary's `--version`.
- CI's standalone AOT smoke publish treats `IL2026`, `IL3050`, and `IL3053` as errors so the app entrypoint stays warning-clean even while the gh extension keeps its local suppression.
- Release artifact uploads keep 30-day retention, and a failed release opens or reuses an issue with the run link for follow-up.
- `.github/workflows/README.md` is the operator-facing overview for CI/release workflow behavior.
- `docs/runbooks/release-rollback.md` is the rollback procedure for live GitHub Releases and the current dark-launch package-manager jobs.
- Homebrew dark-launch scaffolding lives in `packaging/homebrew/skillview.rb.tmpl` and currently generates a formula artifact for the shipped Unix targets (`darwin-arm64` and `linux-amd64`) from published stable-tag assets.
- WinGet dark-launch scaffolding lives in `packaging/winget/` and currently only generates manifest artifacts for package id `harder.SkillView` from published Windows assets.
- Keep package-manager jobs gated behind repo variables (`HOMEBREW_TAP_ENABLED`, `HOMEBREW_TAP_REPO`, `WINGET_ENABLED`) until real publish automation is ready.
