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

## PR #78 review follow-up

The 2026-10-20 static review was checked against the implementation. Saved reviews
now use neutral version-update wording because an ordinary latest-release update
can also resolve to a tag. Only a live explicit tag request is described as a
one-time tag choice; shared guidance replaces drifting CLI/coordinator copies.
No new saved field or schema was introduced just to retain a presentation label.

An explicit tag's HTTP 404 names the tag and repository, while acknowledging that
GitHub can also return 404 for inaccessible resources. Other HTTP errors keep
their original classification, and subprocess diagnostics remain available.
Idle terminal outcomes clear the tag-choice header. Refresh requests during the
chooser retain the latest subject and reload after either accept or cancel;
the captured choice is discarded and the user must choose again from refreshed
inventory. Deactivation likewise cannot prepare the old row. Surrounding pasted
whitespace is trimmed in the modal; CLI and source-boundary validation stays exact.

The encoded-slash concern did not reproduce: read-only GitHub API requests to
`repos/golang/tools/git/ref/tags/gopls%2Fv0.20.0` and its literal-slash equivalent
both returned HTTP 200, `refs/tags/gopls/v0.20.0`, and commit
`2e31135b736b96cd609904370c71563ce5447826`. Existing lightweight/annotated nested-tag
tests remain. Repeated validation guards are intentional entry-point checks that
all call the same validator; they reject bad input before discovery/staging and
also protect direct resolver callers. No separate validation rule was added.

The terminal captures above predate this wording adjustment. Focused renderer,
modal, resolver and Updates lifecycle regressions cover the review changes;
they do not claim a repeat of the full PTY walkthrough.

After the fixes, 136 focused tests passed: resolver 52, tag workflow 12, tag
modal 7, reviewed Updates tab 34, Updates tab 13, update renderer 6, update CLI
11 and source-review CLI 1. Integration smoke tests passed 9/9. Release test/App
and integration builds had zero warnings/errors; scoped formatting and whitespace
checks passed. These are overlapping follow-up runs, not an addition to the
original unique-test count. Hosted checks still need the final published head.

Hosted Ubuntu tests subsequently exposed an existing native-placement test that
required a particular refusal message even though an earlier native identity
check can validly refuse when a shared ancestor changes. Its fixture now points
through dot components to an existing directory with a sentinel: the assertion
requires refusal and unchanged target bytes/link text, without imposing error
priority or relying on a broken target. Native production checks are unchanged.
