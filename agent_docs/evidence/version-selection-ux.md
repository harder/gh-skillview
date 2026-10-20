# One-installation tag choice verification

2026-10-20, Linux ARM64, .NET SDK 10.0.402. Scope is the approved
[tag-only version-selection boundary](../version-selection-scope.md), implemented
through the existing schema-2 saved-update engine. PR #73's broader policy,
approval archives and approved-restore workflows remain deferred.

## Automated verification

165 unique focused tests passed: resolver 52, tag workflow 12, tag modal 5,
recorded-tree stager 7, revision replacement 10, source update review 8,
source-review CLI 1, update CLI 11, gh update adapter 12, reviewed Updates tab 28,
Updates tab 13, and update renderer 6. Repeated follow-up runs are not added to
that total. Integration smoke tests passed 9/9. Locked FDD restore, Release
test/App builds and scoped formatting verification passed with zero build
warnings/errors. No dependencies, lock files or SDK selection changed.

The POSIX protocol fixture resolves an exact requested tag, stages independently
hashed Git objects, captures local changes, refuses apply before source review,
applies the saved candidate and undoes to the starting content. It deletes the
fixture gh executable before apply/undo, proving those actions consume saved
evidence. A same-name independent copy remains unchanged. Pin, broad selection,
dry-run/unpin combinations, malformed tags, provider failures and cancellation
are covered by focused tests. The POSIX subprocess fixture explicitly skips on
Windows; the portable resolver/guard/UI tests and existing native CI remain
separate evidence. No new native mutation mechanism was introduced.

## Actual terminal and CLI walkthrough

The built apphost ran at 80×24 in a real PTY with private HOME, state, CWD and
an isolated gh API/catalog stub. No real credentials or skill code were used.
The fixture had a v2.0 installation with a local note/extra file and a separate
same-name healthy copy. Source blobs and tree IDs were computed from exact bytes.

Typing `v1.0` reached the tag field through the real shell key routing. Enter
prepares review only; the existing source and retention approvals still gate
apply. Escape
canceled without changing any fixture bytes or creating a plan. Preparing the
tag left installed files unchanged and opened the existing source/retention
review. The first walkthrough exposed technical IDs preceding the version; a
small summary now leads with old/new refs, affected agents, local-change count
and retention/latest guidance, with exact evidence retained below.

After both approvals, Apply installed v1.0. Shell checks verified the selected
ref/version, retained original bytes and local extra, and unchanged independent
copy. Leaving Updates and reopening version choice showed current ref v1.0,
proving rescan reached the new installed state. App teardown exited successfully.
An additional built-CLI walkthrough exercised `update-plan --tag`, source
acknowledgement, apply, inspection and undo, restoring every starting fixture
file. CLI JSON and private receipts are under
`/var/tmp/skillview-version-scope/pty/evidence/`.

Captures: [tag input](../../assets/screenshots/version-selection-80x24-choice.txt),
[version-first review](../../assets/screenshots/version-selection-80x24-review.txt),
and [current ref after rescan](../../assets/screenshots/version-selection-80x24-rescan.txt).

This is focused local verification. The full unit suite and Native AOT publishes
were not run locally; hosted CI status must be checked on the published PR head.
