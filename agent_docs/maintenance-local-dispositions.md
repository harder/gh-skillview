# Reviewed local dispositions

Reviewed local dispositions, schema 3 merge plans, CLI entrypoints and owned
inline TUI controls merged in PR #65. The [historical verification record](https://github.com/harder/gh-skillview/blob/0f7264f7eca48f6329f9a82d60fbf803e43dd5bd/agent_docs/evidence/maintenance-batch-15-final.json)
records the completed platform, recovery and TUI checks. The contracts below
remain applicable; references to original audit obligations describe historical
scope, not an instruction to resume the retired maintenance goal.

Implemented workflow boundaries:

- `ThreeWayPayloadReview` analyzes complete path sets, explicit captured-file
  resolutions, root mode, topology and aggregate budgets. It is not authority.
- `CapturedPayloadArchive` retains immutable indexed regular-file blobs for
  file/link bytes, under a held private state directory. Its bounded manifest
  explicitly retains full native identities/generations and observed metadata;
  unsupported ACLs, flags, streams, hard links or unknown namespace coverage
  remain concrete refusals. The legacy `PayloadSnapshot` remains a byte/object
  comparison contract, not a complete metadata attestation.
- `ReviewedPayloadMaterializer` builds selected authored content through
  `NativePayloadDirectory`, rebinds approved source tracking, and finalizes via
  the held root. A successful draft is still not an applyable saved plan.
- `CaptureInto` opts into Unix link modes. Legacy capture, backup and saved
  replacement hash inputs continue omitting those modes. Never compare the
  two capture flavors as if they had identical fingerprints.
- macOS symlink modes can reflect umask. Preserve the observed mode through
  an `O_SYMLINK` handle; never chmod the target. Linux links require 0777.
  Windows materialization still needs captured directory-link kind and full
  native metadata admission before the draft workflow can use links.
- Finalization must stay on the originally created payload handle. Reopening
  a canonical pathname can accept a replacement with identical bytes/modes.

CLI flow (private-stage writes until explicit Apply):

1. `update-plan` produces a schema2 immutable B/C plan without changing I.
2. `update-merge-review --id ID --expect SOURCE_PLAN_HASH` independently retains
   B/I/C blobs plus native metadata and closes the earlier replacement approval.
   Repeat with the same source-plan hash to inspect saved choices after restart.
3. `update-merge-choose --id ID --expect LOCAL_REVIEW_HASH` writes the complete
   explicit choice set with repeated `--choose-installed PATH` or
   `--choose-source PATH`; `/` selects the root mode. Unselected paths use the
   three-way rules. Unknown/duplicate choices and invalid topology are refused.
   `--resolve TARGET FILE` captures exact regular-file bytes/mode/source metadata;
   `--choose-resolved TARGET` reuses previously completed referenced evidence.
   The source pathname stops being an apply dependency after capture. A byte
   reader interface supports resolutions without fabricating a native root.
   Resolution manifests keep real source observations separate from private
   blob metadata. Only target `SKILL.md` receives strict YAML/UTF-8 parsing.
4. `update-merge-plan --id ID --expect LOCAL_REVIEW_HASH` builds M through held
   native handles and saves schema3. It binds the source plan, all manifests,
   exact choices, source C, result M and metadata policy. New C nodes use captured
   C attributes and reviewed I-parent ownership; compatible I kinds keep I
   attributes. Existing root privacy is the default. Unknown metadata refuses.
5. `update-apply --id ID --yes --disposition merge-reviewed --expect MERGE_HASH`
   uses the existing retain/promote/owner journal, with B/I/C/metadata/result
   revalidation. Recovery/undo still require a fresh inspection fingerprint.
6. `update-preserve --id ID --expect PLAN_HASH` verifies and keeps I without any
   payload move, owner write or preference change. The private plan remains.

Choice records are append-only, and invalid newest records cannot revive prior
choices. Finalization attempts use bounded independent `merged-NN` slots and
`merge-plan-NN.json` records; partial evidence survives retries. Attempt presence
is independent of the current payload location after promotion. An incomplete
initial B/I/C capture remains retained and requires a new revision plan; it never
restores approval of the earlier source replacement. Cross-platform acceptance
for the implemented preserve/merge workflows and their TUI/recovery proofs is
complete; see [historical verification record](https://github.com/harder/gh-skillview/blob/0f7264f7eca48f6329f9a82d60fbf803e43dd5bd/agent_docs/evidence/maintenance-batch-15-final.json). Resolution storage
includes failed/unreferenced attempts: at most 16 archives, 256 MiB of raw bytes
and 64 MiB of encoded manifests per plan. Bound the outer draft enumeration
before computing that quota. Flush each archive, then the reopened draft namespace
and operation writer/device barrier before publishing any reference in a choice
record.

Replacement supports exact recorded
restore and selected newer GitHub revision, retaining original/displaced native
objects. It admits no owner or one verified shared gh v3 owner. It already stages
recorded base and candidate and lists local differences. The saved workflow supports
preserve and reviewed merge alongside retain-original replacement. Durable fork,
source/other-owner transitions and the remaining proof matrix below describe
historical scope; new work requires a concrete user need.

## Three-way review

Capture three independently verified whole payloads: recorded immutable base B,
installed I, approved candidate C. Retain raw bytes, links and modes; compare authored
SKILL metadata with documented installer-field normalization only. Compare complete
path sets and root mode, including additions/deletions/empty directories, type
changes, link bytes and executable flags. A matching display version is irrelevant.

For each path: I=B -> C; C=B -> I; I=C -> that value; otherwise conflict. Missing
entries participate as values. A path's ancestor type or deletion can conflict with
a descendant, so reject an inconsistent merged topology before materialization.
Case-fold collisions follow the target filesystem's actual behavior; lexical
case-insensitive comparison cannot collapse two source paths. Unknown capture or
unsupported metadata cannot establish a merge. Existing payload is never modified
while analysis or conflict resolution is incomplete.

Offer explicit actions:
- Preserve installed: check/diff only, no install or native-owner write. Record an
  external manual-maintenance preference only if separately requested.
- Merge reviewed local changes with approved source: build a new private candidate
  from the path-level choices. No implicit textual merge or conflict markers in
  authored instructions. Nonoverlapping path changes may be proposed, but review
  the complete result and every unresolved conflict before approval. A conflict
  needs an explicit installed/candidate or exact reviewed resolved-file choice.
- Replace with original retained: existing durable native retain/promote path,
  with explicit discarded-local-path list and affected exposures.
- Keep fork: define ownership and future-update policy, not merely copy a badge.
  For a shared physical install, show all affected consumers. A per-consumer fork
  requires a topology/detach plan, not a path-only overwrite. Native owner
  tracking and installer source fields must remain truthful. Preserve origin and
  original source evidence outside payload; never fabricate a remote tree hash for
  customized bytes. Where native gh tracking can represent the fork, stage via its
  documented local-source capabilities; otherwise retain original ownership until
  an explicit supported owner transition or export/detach completes.
- Cancel: leave live objects, preferences and owner records unchanged; private
  inspected evidence may remain with an explicit retained-state report.

Plan formats must not silently alter old schema1/schema2 hash
inputs. Add independently versioned disposition/source/owner transition records
or a new schema with a demonstrated old-native compatibility boundary. Bind exact
B/I/C payloads, original/native generations, selected path choices, final materialized
candidate and its metadata, all affected exposures, owner before/desired entry
images, external preferences and the review fingerprint. Revalidate at apply.

## Source and ownership boundaries

Reviewed local merges preserve approved source C as the future comparison
baseline while result M has its own exact evidence. They do not authorize
per-consumer detach, durable forks, other-manager adoption or arbitrary remote
source switches. Those workflows remain deferred under
[project scope](deferred-maintenance-work.md).

Supported [local reimport](maintenance-local-reimport.md),
[local source switching](maintenance-local-source-switch.md) and
[standalone unpin](maintenance-standalone-unpin.md) use their own reviewed plans,
pin rules and native owner journals. Reuse those contracts; do not infer new
mutation authority from the three-way path comparison.

## Proof matrix

Changed tracked file, extra file, local deletion, mode/link/empty-directory change,
unchanged upstream tree, same-name different-origin/context, pin and explicit unpin,
shared consumers; nonoverlap merge, same-path and ancestor/type
conflict, source switch and local reimport. Verify review-fingerprint staleness,
disk/cancellation/process-kill at each journal boundary, concurrent owner mutation,
restart recovery, repeat no-op and undo restoring exact originals/extras/exclusions.
Test actual gh minimum/current behavior, preserving observed help/runtime mismatch.
Use applicable native hosts and recovery checks for supported operations. Deferred
fork/detach/other-manager cases are not an implementation or acceptance backlog.

Additional invariants:
- A merged result M records approved source C as the future comparison baseline
  and a separate exact local-delta/result fingerprint. Native recorded tree means
  source baseline, not a claim that customized M is byte-identical to C. Preserve
  that distinction in every output and next update; verify the next three-way
  comparison uses C, not the already customized M as an invented pristine source.
- A mutable local source or missing historical base cannot stand in for B. Use a
  previously retained immutable baseline if proven, or require a separate explicit
  baseline-less preserve/fork/replace decision; never claim an automatic merge.
- Standalone unpin is separate from unpin-and-update. Clear only verified pin
  policy/tracking with exact local files/extras retained, using a saved owner+
  metadata transaction. Actual gh --unpin update behavior is not this operation.
- Installed and user-provided resolution bytes must be captured into immutable
  private stage blobs before approval; paths alone are insufficient. Source and
  original revisions/targets can change before Apply and must be revalidated.
- Current GhRecordedTreeStager rejects Git symlink mode120000. Do not promise link
  materialization until raw native link creation is implemented and tested. Raw
  link-byte snapshot/undo already exists; merge publication needs its own boundary.
- PayloadSnapshot attests bytes, Unix modes and link bytes, not complete ACL/xattr/
  ADS metadata. Retained originals preserve native rollback. A proposed merged
  candidate must explicitly preserve supported local metadata or refuse unsupported
  cases; do not silently claim arbitrary metadata preservation from path algebra.
  Preserve the original root privacy/mode unless separately reviewed otherwise.

- Cross-store apply/recover/undo needs an explicit before/desired/foreign matrix
  for payload, native owner and preferences. Undo only the policy generation
  changed by this plan; preserve later independent set/clear actions. Reserve
  publication and undo capacity before displacement and document lock ordering.
