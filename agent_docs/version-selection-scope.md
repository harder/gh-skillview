# One-installation version selection: bounded comparison

Assessment date: 2026-10-20. This is a source comparison and recommendation,
not product implementation or runtime verification. The authoritative project
goal is the [scope ledger](deferred-maintenance-work.md). The original all-21
objective is retired.

## Inspected revisions

- Current fetched main: `a9703a75273bdc9f3f0c2b5851bd224ccfcdc3e2`.
- PR #73: `3fd29566cde0e9b8eeb2b48a0941f93defb28459`.
- Its declared stacked PR #72 base: `4e77ee5d3c1732448fc1e0265156ba81baf71729`.
  The PR-only change from that base is 84 files, 8,205 additions and 259 deletions.
  This size describes the broader PR, not the effort for the proposed slice.
- PR #77 remains separate; reconcile its final merged main before publication
  when available. The user approved implementation while its CI was pending.
  Original dirty checkouts and private PR #73 fixes were not modified.

## What exists and what is missing

| User need | Current main | Relevant PR #73 code and decision |
| --- | --- | --- |
| Choose exactly one installed copy | CLI `--path`/`--install-id`; Updates current-row selection and captured physical targets | Reuse main selection and reconciliation. Selecting an agent does not isolate a shared physical payload. |
| Choose an older or different version | `GhRevisionResolver.ResolveLatestAsync` chooses latest release, or default branch when no release exists; there is no arbitrary-version input in ordinary Updates | Reuse the exact-tag resolution/validation ideas and tests in `GhRevisionResolver.Selected.cs` and `GhSelectedRevisionTests.cs`. |
| Freeze what will be installed | `ReplacementPlanStore.Update.cs` resolves immutable commit/tree, stages candidate and recorded base, then saves schema 2 | Reuse main's saved update path for a tag. Schema 2 already accepts `refs/tags/...`; it rejects bare commits and pinned desired results. |
| Understand local edits, consumers and recovery | `SavedUpdateCoordinator`, `UpdatesTabView.Review.cs`, source review, retained original, inspect/recover/undo | Reuse these flows. PR #73 only extends coordinator apply dispatch; it does not add selected-version input to the ordinary Updates tab. |
| Apply a selected tag/full commit with optional pin | PR #73 adds schema 7, actual gh-output witnesses, extra archives and selection-aware owner/lifecycle validation | Do not import this path wholesale for the first tag-only slice. It is coupled to desired-policy admission and receipts. |
| Keep approved content as persistent desired state and restore it later | PR #73 adds policy stores/gates, approval archives, schema 8 and approval UI/CLI | Preserve the branch and its documentation; defer this product surface. It is not required to choose a tag once. |

The reusable core is real, but PR #73 is not a small drop-in UI change.
`ReplacementPlanStore.Selected.cs` acquires desired policy, captures raw source
and actual gh output, and carries selection through native publication/recovery.
`ApprovedPayloadStore`, `DesiredRevisionPolicy*`, `ApprovedPayloadScreen` and
`SkillViewWorkflowCoordinator.Approvals.cs` belong to the deferred broader flow.
Its guides explicitly qualify missing positive native evidence. This assessment
does not establish that those failures or portability requirements are resolved.

## Recommended first implementation

Offer **Choose version...** for the current physical row in Updates. Start with
an explicit **Git tag** input, showing the current source/ref and an example.
Prepare the requested tag from the same repository and recorded skill subpath;
show its resolved commit and affected consumers in the existing review. Apply
the saved candidate through the existing source/retention approvals, then rescan.
This also permits a deliberate downgrade when the existing validators admit it.

Proposed CLI entry: `update-plan --path PATH --tag TAG` (or `--install-id`),
followed by existing `update-ack-source`/`update-apply` and inspection/undo.
No new top-level lifecycle command family is needed. The ordinary update command
continues to mean latest. Version selection targets one row, independently of
marks or All; it does not batch the same tag across unrelated installations.

An explicit tag request must never fall back to latest/default on failure.
Resolve once to immutable objects; apply/recovery must not query a moving tag
again. Preserve the existing repository/subpath/name, owner and metadata checks,
captured child context, local-change review, quotas and cancellation handling.
The same schema-2 staging path already installs a release tag for latest updates;
the intended change is source selection, not new mutation authority.

**Limits must be visible:** this is a one-time version change, not a persistent
desired-version policy. A later ordinary Update follows the existing latest
policy. A pinned installation remains protected; the first slice requires the
existing separately reviewed unpin operation before version selection. Creating
or changing a pin, raw full commits, arbitrary branches, tag browsing, source
switches, detaching consumers and automatic consolidation are deferred. Full
commits cannot be put into schema 2 by inventing a symbolic branch name.

## Small implementation boundary and finish point

Expected changes are the resolver/tag validation, a shared source-selection
entry into the existing update planner, coordinator and CLI routing, one-row
Updates input/review wiring, and focused tests/user guidance. Reuse selected-tag
tests from PR #73 where applicable. Do not cherry-pick its broad commits or
replace newer main/PR #77 UI work with its older tree.

Finish when these user cases pass:

1. A chosen tag resolves exactly, including annotated/nested tags; invalid,
   missing or inaccessible tags refuse without fallback or installed writes.
2. Review/cancel leaves installed content unchanged; source and retention
   acknowledgements remain required before apply. Local edits remain visible.
3. Apply changes only the selected physical installation; same-name independent
   copies stay unchanged. Shared consumers are shown and keep their links.
4. Existing pins refuse the new action without silently clearing or replacing
   policy. A later ordinary Update's latest behavior is explained in review.
5. The saved candidate remains authoritative if the remote tag later moves;
   apply/undo use existing owner-preserving and retained-original behavior.
6. A real narrow-terminal walkthrough proves choose tag, review, cancel,
   apply and rescan. Appropriate managed tests and affected native CI pass.

No product code was changed or built for this comparison. Existing test/source
presence is evidence for reuse, not proof of the proposed new entry point.
If implementation requires a new schema, approval store, provider witness or
native mutation mechanism, stop and revisit this boundary before expanding it.
The user approved this scope and its limitations on 2026-10-20. Implementation is
on `codex/version-selection-scope`; the comparison above remains historical
evidence for the boundary, not runtime proof of the implementation.
Focused implementation and terminal verification are recorded in
[the verification note](evidence/version-selection-ux.md). Hosted CI must still
be assessed on the published implementation head.
