# Release rollback

Use this runbook when a `release.yml` run publishes a bad `gh-skillview` / `skillview` release or fails partway through.

## Scope

- **Live channel today:** GitHub Releases published by `.github/workflows/release.yml`
- **Live Homebrew channel:** `harder/homebrew-tap` follows stable releases after validating the formula on macOS ARM64 and Linux x64
- **Dark-launch only:** `publish-winget` generates artifacts but does **not** submit upstream

## Triage first

1. Open the failed or suspect workflow run.
2. Determine whether the bad state is:
   - a failed build with no release published
   - an unpublished draft release with incomplete assets
   - a published GitHub Release with bad binaries or metadata
   - a Homebrew formula PR or merged tap update
   - only generated WinGet artifacts
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

## Homebrew tap rollback

`publish-homebrew` uploads a verified formula artifact. The separate tap
workflow opens a formula PR for each newer stable release and merges it only
after its macOS and Linux checks pass.

1. If the tap PR has not merged, close it or fix its formula, then rerun the
   tap validation. The existing published formula remains available.
2. If the formula has merged and is bad, revert that tap commit and let tap CI
   validate the revert. Inspect the release binaries before republishing.
3. Do not edit or reuse the published release's bytes. Fix the cause and
   publish a new SkillView version, then manually dispatch the tap's
   **Update SkillView formula** workflow to check it promptly.

## WinGet artifact rollback

`publish-winget` only uploads generated manifest artifacts. Discard artifacts
from a failed run and keep `WINGET_ENABLED` disabled until the fix is verified.

## After rollback

1. Update or close the failure issue opened by `notify-failure`.
2. Link the fixed commit or replacement tag.
3. Note whether the rollback was:
   - release deletion only
   - release deletion plus commit revert/fix
   - Homebrew tap revert or PR closure
   - WinGet artifact discard only
4. If the failure exposed a missing safeguard, update:
   - `.github/workflows/release.yml`
   - `.github/workflows/README.md`
   - `agent_docs/release-engineering.md`
   - this runbook
