# Update outcomes and no-op checks

Product updates use `SavedUpdateCoordinator`: prepare one exact candidate, review
the saved manifest, explicitly retain the original, then apply. `--force` never
approves the disposition. `SkillUpdateCoordinator` remains a low-level actual-gh
contract adapter; product callers use only its availability path.

An unchanged check compares payload content, root mode, tracking policy and owner
transition. A fresh check of the same immutable tree can additionally recognize
gh's YAML map order, string presentation and delimiter newline handling without
rewriting installed files. The bounded comparison includes the complete YAML
structure and every other payload entry/mode; ambiguous implicit scalar types,
tags, anchors and duplicate keys cannot authorize that fallback. It does not
apply to new tree revisions or saved-plan Apply/recovery. Presentation-only local
differences remain untouched and can be considered semantically current; changed
authored values, body content, sequence order, extras and modes require review.
Ref/pin and owner transitions also require review. A fresh equal
check leaves installed native objects and registry bytes/timestamps untouched and
creates no saved plan. It removes only its never-published private staging through
ownership duplicated from the already-held state handle. Never reopen a pathname
to establish that cleanup authority. Cleanup uses an independent bounded token;
failure leaves a visible exact private location and a failed result. Cancellation
still reports 130 after private cleanup quiesces. Revalidate the installed objects
and context after cleanup before reporting unchanged.

For a current installation, `update-plan --json` returns `operation: null` and an
`unchanged` object; `succeeded` is false if private cleanup was not verified.
Ordinary `update --json` returns an `unchanged` installation outcome with
`savedPlan: null`, zero changed files and `mutationAttempted: false`. Neither
result invents a plan ID or an Apply command.

The strict live/candidate metadata snapshots must match both raw payload and
native object-state fingerprints of their held directory captures. Evidence-only
captures intentionally do not normalize frontmatter; do not use their
`ComparisonFingerprint` as a semantic comparison. gh's serialization behavior is
defined in its [frontmatter implementation](https://github.com/cli/cli/blob/v2.102.0/internal/skills/frontmatter/frontmatter.go).

Legacy equal saved plans remain inspectable. Apply or forward recovery of a
never-started, freshly verified ready plan returns unchanged without a move,
registry write or journal. An interrupted plan with a vacant original slot must
still recover, even if original and candidate bytes match. Do not short-circuit
started journals or owner repair on payload equality.

Schema-2 plans written before no-op preservation may contain the old selected
owner rewrite even when tree, pin and semantic fingerprint are unchanged.
Validation accepts either the exact fresh before-image or the exact historical
tree/pin/timestamp serialization, checking both saved bytes and fingerprints.
`RecreateSavedTransition` is validation-only; never use it for fresh preparation.
Persisted legacy fixtures cover different and equal timestamps, interrupted
payload moves, forward recovery, undo and rejection of changed extension fields.

`UpdateResultAccumulator` retains bounded stdout, stderr, notices, parsed entries,
errors and failure/cancellation state across one captured UI queue. It must never
reuse an earlier saved plan as a later Apply target. The queue owner retains exact
installation outcomes separately and resets both stores at the next capture.
Omitted output is explicit. Failed source requests retain both bounded diagnostic
streams through staging wrappers and CLI JSON/plaintext; successful Git blob/tree
responses remain payload inputs. Rendered diagnostics use the shared sanitizer.

Verification:

- `SharedReplacementRevisionTests`: equal apply/recovery, repeat checks beyond
  plan quota, owner byte preservation, same-tree repair, interrupted recovery,
  stage failures, canceled cleanup and operation-name replacement.
- `UpdateResultAccumulatorTests` and `ReviewedUpdatesTabTests`: queue output,
  failure/cancellation retention, resource bounds and fresh queue reset.
- `GhPresentationComparisonTests` and snapshot/replacement fixtures: gh formatting
  controls, unknown nested structures, body spaces/trailing lines, ambiguous YAML,
  new revisions, other entries/modes and equal-byte candidate replacement.
- Opt-in `GhUpdateOutcomeContractTests`: actual-gh partial batch and the actual
  product saved-update workflow driven through `UpdatesTabView`. Run with the
  standard contract flag and both minimum/current gh hosts.
- `tests/native/prove-update-outcomes.py --app <native-host> --output
  <proof.json>`: actual pinned/untracked/updated/current/same-tree-force-repair
  and later remote failure in a private home, CWD, state and Git context. It
  checks payload objects, bytes, modes, owner bytes, retained originals, exact
  changed paths and both failure streams. Python inode checks are supplementary;
  cross-platform unit tests enforce full native identity semantics.

The native proof does not inject cancellation or claim an active agent session
reloaded. Cancellation and independent post-attempt reconciliation are covered by
the dedicated ownership tests; process-death recovery has separate native proofs.
