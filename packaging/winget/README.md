# WinGet submission

`harder.SkillView` packages the standalone Windows `skillview` command. It does
not register the `gh skillview` extension; install that separately with
`gh extension install harder/gh-skillview` if you prefer the extension entrypoint.

The [1.0.0 manifest set](submission/manifests/h/harder/SkillView/1.0.0/) is
ready to copy to `manifests/h/harder/SkillView/1.0.0/` in
[`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs). It uses
the published Windows x64 and ARM64 executables, checksums from the release,
`InstallerType: portable`, the `skillview` command alias, and a `GitHub.cli`
dependency with the minimum supported version.

To regenerate a manifest set from a future stable release:

```powershell
gh release download v1.0.0 --repo harder/gh-skillview --pattern '*windows*' --dir artifacts
./packaging/winget/New-WinGetManifest.ps1 -ReleaseRef v1.0.0 -AssetsDir artifacts -OutputDir generated/winget
winget validate --manifest generated/winget/manifests/h/harder/SkillView/1.0.0
```

The script verifies all four Windows binaries against the published checksum
files before rendering the three version-specific manifests. Replace `v1.0.0`
with the release tag when preparing a newer version. The opt-in release job
performs the same generation and uploads the manifests as an artifact when
`WINGET_ENABLED` is `true`; it does not submit a PR upstream.

Before submission, install the manifest in Windows Sandbox or a clean Windows
environment, confirm `skillview --version` and `skillview --help`, then
uninstall it. Local manifest installation requires an administrator to enable
`LocalManifestFiles` with `winget settings --enable LocalManifestFiles`.
Restore the previous setting with `winget settings --disable LocalManifestFiles`
afterward if it was disabled before testing. The community repository accepts
one version and only manifest files per PR.
