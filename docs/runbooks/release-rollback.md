# Release rollback

Use this runbook when a `release.yml` run publishes a bad `gh-skillview` / `skillview` release or fails partway through.

## Scope

- **Live channel today:** GitHub Releases published by `.github/workflows/release.yml`
- **Dark-launch only today:** Homebrew / WinGet generation jobs (`publish-homebrew`, `publish-winget`) create artifacts but do **not** publish externally yet

## Triage first

1. Open the failed or suspect workflow run.
2. Determine whether the bad state is:
   - a failed build with no release published
   - an unpublished draft release with incomplete assets
   - a published GitHub Release with bad binaries or metadata
   - only generated Homebrew / WinGet artifacts from dark-launch jobs
3. Capture the run URL and note the affected ref/tag in the failure issue, if one exists.

## GitHub Release rollback

If a draft is incomplete, inspect the failed run and delete the **draft**
before retrying the same tag. A published release is immutable when the
repository setting is enabled: its assets cannot be replaced. Withdraw the
affected release if necessary, fix the code, and publish a **new version tag**.
Never reuse the withdrawn tag name.

For a published release that must be withdrawn:

```bash
gh release delete <tag> --repo harder/gh-skillview --cleanup-tag -y
```

After deletion:

1. Confirm the release is gone:

   ```bash
   gh release view <tag> --repo harder/gh-skillview
   ```

   This should fail once rollback is complete.

2. Confirm the remote tag is gone:

   ```bash
   git ls-remote --tags origin | grep "<tag>"
   ```

3. If the underlying commit is also bad, revert or fix it on `main` before cutting a replacement tag.
4. Publish from a new corrected tag only after the replacement build is
   verified locally and in CI. Immutable release tags cannot be reused after
   their release is deleted.

## Failed release before publish

If `build` failed or the workflow stopped before the `release` job published assets:

1. Check for an incomplete draft release and remove it before retrying.
2. Fix the underlying issue on `main`.
3. Re-run the workflow on the existing tag only if no release was published.
   If it was live, cut a new tag after the fix.

## Homebrew / WinGet dark-launch rollback

Today these jobs only generate artifacts:

- `publish-homebrew` uploads a generated formula artifact
- `publish-winget` uploads generated manifest artifacts

There is no external tap push or WinGet submission yet, so rollback is just:

1. Treat the generated artifacts as disposable.
2. Do not promote or reuse artifacts from the failed run.
3. Keep the gating repo variables disabled until the next validation pass if the scaffold itself was the problem.

No public Homebrew or WinGet rollback is needed until those channels are activated.

## After rollback

1. Update or close the failure issue opened by `notify-failure`.
2. Link the fixed commit or replacement tag.
3. Note whether the rollback was:
   - release deletion only
   - release deletion plus commit revert/fix
   - dark-launch artifact discard only
4. If the failure exposed a missing safeguard, update:
   - `.github/workflows/release.yml`
   - `.github/workflows/README.md`
   - `agent_docs/release-engineering.md`
   - this runbook
