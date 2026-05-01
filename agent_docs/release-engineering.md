# Release engineering

- The release workflow lives in `.github/workflows/release.yml`.
- Publish Native AOT artifacts for six RIDs on native runners: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.
- Release assets come in two families:
  - GitHub CLI extension binaries use Go-style names for `gh extension install`: `gh-skillview-<go-os-arch>[.exe]`
  - Standalone binaries use .NET RID names: `skillview-<rid>[.exe]`
- `cli/gh-extension-precompile@v2` is the release publisher and attestation source. Keep `script/build-noop.sh` as the no-op build override for that action.
- Linux AOT publish still needs `clang` and `zlib1g-dev`.
- Keep `workflow_dispatch` enabled so release packaging can be exercised without pushing a tag.
