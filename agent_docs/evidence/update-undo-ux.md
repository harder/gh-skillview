# Undo an update from its TUI result

Bounded follow-up to PR #80, based on
`8e0e41d13250eba0aef1d0a2cdb766c7eb719470`. This adds a result-view shortcut
for one successfully applied revision update, including a reviewed local merge.
It reuses saved-plan inspection and native undo; no schema, history manager,
owner repair or native mutation mechanism is added.

## Contract

- Only a successful, uncanceled, reconciled revision update with an observed
  mutation offers Undo. Availability checks, preparation, standalone unpin,
  failed/partial operations and other saved purposes do not offer it.
- Opening Undo captures fresh inventory with the original hidden-directory,
  workspace/profile and child-context settings. Native inspection must identify
  the same saved ID, immutable plan fingerprint, recorded subject and address,
  with the update still installed. It captures a current observation fingerprint.
- The review names the exact location, recorded shared consumers and original
  ref/authored version. F2 retains saved evidence and the fresh inspection.
  A separate restoration checkbox enables Undo update; neither opening F2 nor
  earlier source/retention approvals grant undo approval. Cancel permits another
  fresh inspection without changing installed files.
- Apply uses the inspected observation fingerprint with the existing
  `Recover(..., undo: true, expectedPurpose: "update-revision")` contract. Changes
  to payload, native identities, owner state or context can refuse it. This is
  explicit restoration of retained content, not a new source resolution.
- Owned work retains its lease through queued UI completion. Teardown cancels
  admission/delivery; post-attempt cache invalidation and deadline-bounded
  reconciliation survive view cancellation. Every attempt closes the approval;
  no automatic retry or implicit forward recovery is started.
- The result retains diagnostics and directs an inventory refresh. The shortcut
  ends on refresh, leaving the view, starting another review or attempting undo.
  Existing CLI inspection/recovery remains available for other cases. Agent
  reload is unverified; shared consumers all use the restored physical payload.

## Local verification

**577 UI tests, 14 saved tag/update workflow tests and 9 integration tests passed
(600 distinct tests).** Release test, App and integration builds passed with
zero warnings/errors. Full-solution format verification and whitespace checks
passed; dependency and lock files are unchanged.
After the final refused-outcome presentation correction, the affected 71 reviewed
update tests, 4 update keyboard tests and 9 integration tests were rerun and
passed, with clean Release test/App builds and format verification.

The workflow tests execute native apply/undo from frozen source evidence without
resolving upstream again. Changes before inspection and after inspection refuse
without overwriting the edit, retained original or independent copy. UI tests
cover exact plan/address/subject/fingerprint binding, separate approval, cancel
and retry, narrow/wider layout, refusal without automatic retry, queued completion
ownership and invalidation after canceled delivery. Actual ANSI keyboard routing
proves F2, Esc, and natural Tab → Details → approval → Undo update.

An isolated real **80×24 PTY** used the built host with private HOME, CWD,
config/state/cache and neutralized Git context on Linux WSL2 ARM64. A local gh
protocol fixture supplied four same-name copies and frozen source objects; no
credentials, external network, payload scripts or AI clients were used.

1. Choose v1 for one v2 copy, review source/retention, and apply. Only that copy
   changed; its original local note and extra file remained retained.
2. Open undo, inspect F2, cancel and reopen. The fresh review clearly shows v2
   restoration and affected consumers; approval is initially clear each time.
3. Approve and execute native Undo from the TUI. Refresh shows v2 at the same
   location. All six fixture payload-file hashes match their original baseline,
   including both local edits/extras and all independent-copy files.
4. Apply again and inspect undo. Add a new local edit after inspection, then
   approve and attempt Undo. Native validation refuses. The new edit, all
   independent copies and the retained original remain unchanged by that attempt.

The first terminal pass exposed a cancel/retry footer transition issue. Attaching
undo controls after their existing footer dependencies fixed it; real PTY
cancel/F2/reopen and frame checks verify the retry control remains usable.
Natural Tab order puts restoration approval before the execution button.
Technical paths remain in F2; long forward-result notices follow the main plan
summary so location/version/consumers remain readable first.
The refusal walkthrough also caught the summary reusing the earlier inspection's
installed state. It now reports the terminal refused outcome and omits that stale
current-state assertion; the regression test checks both. Saved evidence is still
retained in Details.

Private logs, transcripts and hash receipts are under
`/var/tmp/skillview-update-undo-ux/`. Retained state/logs are outside payload hash
comparisons. Native Windows/macOS and live upstream behavior were not re-proven.
The broader suite's 13 pre-existing host failures recorded with PR #80 were not
rerun or made a release gate here. Hosted CI must be assessed on the published
head; this is not release or universal recovery proof.

Representative captures:

- [Undo review](../../assets/screenshots/update-undo-80x24-review.txt)
- [Original restored](../../assets/screenshots/update-undo-80x24-restored.txt)
