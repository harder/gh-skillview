# Cleanup review UX verification

Verified on Linux ARM64 with .NET SDK 10.0.402 using the built app and a private scan root, HOME, state directory, CWD, and local `gh` stub. The fixture contained a dangling symlink, a literally empty directory, and one unrelated valid skill.

At 80×24, Cleanup listed the broken link and empty directory as separate candidates. The summary states what each action removes, warns that deletion has no automatic undo, and leaves the complete Location available by focusing the detail pane with Tab and scrolling with PgDn. F2 toggles the technical details. At 120×36, the full empty-directory Location was visible in the confirmation view. Cancel was initially focused and Enter returned without mutation. After reviewing one selected item, one Tab focused Remove and Enter applied only that item.

The PTY walkthrough removed the dangling link and the empty directory in separate Review → Apply operations. Each result showed `removed 1, skipped 0, failed 0`. Closing Cleanup with Esc returned to the parent inventory; after each removal, the removed row was absent on rescan. Shell checks confirmed both paths were gone and the unrelated healthy `SKILL.md` SHA-256 remained `6f03181395dbdb353f16f26a18af38a2f320785e5954b2cce3e258dc24591eae`.

A deterministic UI test pauses removal before the second empty directory, sends Esc, then releases the worker. It verifies the dialog drains before closing and retains the first completed removal for refresh. This covers cancellation timing deterministically; the PTY walkthrough did not attempt a timing-sensitive cancellation against a fast local operation.

Validation on the final source: `CleanupClassifierTests` 19/19, `CleanupScreenTests` 26/26, `CleanupSuppressionScreenTests` 6/6, and integration smoke tests 9/9 passed. The Release app and test/integration builds succeeded; scoped `dotnet format --verify-no-changes` and `git diff --check` passed. Logs are retained under `/var/tmp/skillview-cleanup-ux/evidence/`.

Captures: [80×24 overview](../../assets/screenshots/cleanup-review-80x24-overview.txt), [80×24 scrolled Location](../../assets/screenshots/cleanup-review-80x24-location-scroll.txt), [80×24 confirmation with Cancel focused](../../assets/screenshots/cleanup-review-80x24-confirm-cancel.txt), and [120×36 confirmation details](../../assets/screenshots/cleanup-review-120x36-confirm-details.txt).
