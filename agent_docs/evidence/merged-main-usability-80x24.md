# Merged-main 80×24 usability pass

Base: `33207ae`, the PR #81 merge. This bounded pass used the built Release App
on Linux ARM64 in a real 80×24 PTY. A private local `gh` fixture supplied
inventory and frozen source responses; HOME, config, state, cache, CWD and Git
context were isolated under `/var/tmp/skillview-merged-usability-20261021/pty`.
No credentials, network requests, skill scripts or AI clients were used.

The fixture had v2 and v1 copies from one recorded source, an identical v2
copy, a same-name copy from another source, a link exposing the v2 physical
installation through a second root, a broken link and local edits in the v2
payload (`SKILL.md` note and `extra.txt`). The private fixture and PTY raw
transcript are local evidence; they are not a release artifact.

## Observed path

1. Changes showed the broken link's cause, exact link-only effect and lack of
   automatic Undo. Enter opened a cleanup review with exactly one candidate,
   `broken/lost`. Esc returned without selecting or removing it.
2. A copy-conflict row opened Copies and versions. Selecting the v1 copy and
   comparing with v2 showed different recorded revisions and two differing
   files, without declaring either revision preferred. The identical v2 and
   other-source copies remained separately listed.
3. Update / version opened Updates for exactly `older/demo`. Choose version
   repeated that location and current `refs/tags/v1.0`; Esc reported that no
   candidate was prepared. The shared physical copy's drill-in listed its
   recorded consumers and stated that they share the files.
4. The review-only copy-conflict summary exposed one misleading line:
   “Deletion has no automatic undo” beside “Inspection only; no automatic
   removal.” It now says “No change to undo; this issue opens review only.”
   A fresh real 80×24 PTY capture of the corrected view is
   [here](../../assets/screenshots/changes-review-only-80x24.txt).

All six original fixture payload files kept their SHA-256 hashes. The shared
link and broken link stayed in place. The local `gh` stub logged 54 calls in
the first walkthrough and no mutating `gh` command. The second presentation
check also exited without a mutation. The terminal run reviewed and canceled
actions; it did not exercise native apply or deletion.

The previous [update review](update-review-ux.md) and
[Undo result](update-undo-ux.md) walkthroughs cover the saved-plan apply/Undo
handoff, exact copy restoration and refusal after a new local edit. This pass
did not repeat native Undo because the follow-up changes only the Changes
summary wording. Native Windows/macOS behavior, live upstream compatibility,
agent-session reload and release availability remain outside this pass.

Focused `MaintenanceQueueTests` passed 17/17, integration tests passed 9/9,
and the Release App build had zero warnings or errors. The review-only and
removable-row assertions keep the deletion warning on real removal routes.
