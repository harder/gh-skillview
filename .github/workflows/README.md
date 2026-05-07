# CI/CD Workflows

## Workflows

### `ci.yml` — Continuous Integration

Runs on pushes and pull requests targeting `main`.

- Restores dependencies
- Builds in `Release`
- Runs the full .NET test suite
- Runs three-RID AOT publish smoke coverage (`linux-x64`, `osx-arm64`, `win-x64`)

### `release.yml` — Build, verify, and publish tagged releases

**Triggers**

| Trigger | When |
|---|---|
| Tag push | `v*` tags |
| `workflow_dispatch` | Manual reruns / release packaging exercises |

**Pipeline**

```text
build (6 RIDs, restore/build/test/publish) -> release
                                             -> publish-homebrew (dark-launch, stable tags only, opt-in)
                                             -> publish-winget (dark-launch, stable tags only, opt-in)
                                             -> notify-failure (on error)
```

**Build matrix**

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

Each build leg now:

1. restores
2. builds in `Release`
3. runs the full .NET test suite
4. publishes both Native AOT binaries
5. uploads staged assets as artifacts

Release runs are serialized with a workflow-level concurrency lock so two publishes cannot overlap.

**Artifacts**

- GitHub CLI extension binaries: `gh-skillview-<go-os-arch>[.exe]`
- Standalone binaries: `skillview-<rid>[.exe]`

Artifacts are uploaded per RID and then merged by the final release job, which publishes via `cli/gh-extension-precompile@v2` and generates attestations.

**Package-manager dark launch**

- `publish-homebrew` is gated behind `HOMEBREW_TAP_ENABLED == true` and generates a formula artifact from `packaging/homebrew/skillview.rb.tmpl`.
- Recommended future tap target: `harder/homebrew-tap` via repo variable `HOMEBREW_TAP_REPO`.
- `publish-winget` is gated behind `WINGET_ENABLED == true` and generates manifest artifacts from `packaging/winget/`.
- Both jobs stop at generated artifacts today; they do **not** push to a tap repo or submit to `winget-pkgs` yet.

**Failure handling**

If any release job fails, `notify-failure` opens or reuses an open GitHub issue titled `Release workflow failed on <ref>` with a link to the failed run.

## Related docs

- `agent_docs/release-engineering.md`
- `docs/runbooks/release-rollback.md` *(planned in backlog item #8)*
