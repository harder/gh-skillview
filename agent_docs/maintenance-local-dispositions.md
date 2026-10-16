# Full M08 work retained after external suppression

External suppression merged in PR64. Batch15 is implementing reviewed local
dispositions on `codex/reviewed-local-dispositions`; it has no PR yet. The private
draft capture/materialization code does not admit publication. Complete metadata
admission, immutable plan binding, CLI/TUI review and recovery proofs before
publishing a coherent batch. Original M08 acceptance remains open.

Current private draft boundaries:
- `ThreeWayPayloadReview` analyzes complete path sets, explicit captured-file
  resolutions, root mode, topology and aggregate budgets. It is not authority.
- `CapturedPayloadArchive` retains immutable indexed regular-file blobs for
  file/link bytes, under a held private state directory. It does not attest
  ACLs, xattrs, alternate streams or hard-link topology.
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

Original acceptance stays unchanged. Existing replacement supports exact recorded
restore and selected newer GitHub revision, retaining original/displaced native
objects. It admits no owner or one verified shared gh v3 owner. It already stages
recorded base and candidate and lists local differences, but only offers reviewed
retain-original replacement. Preserve/merge/fork and source/other-owner transitions
remain required.

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
  requires a topology/detach plan (M13/M14), not a path-only overwrite. Native owner
  tracking and installer source fields must remain truthful. Preserve origin and
  original source evidence outside payload; never fabricate a remote tree hash for
  customized bytes. Where native gh tracking can represent the fork, stage via its
  documented local-source capabilities; otherwise retain original ownership until
  an explicit supported owner transition or export/detach completes.
- Cancel: leave live objects, preferences and owner records unchanged; private
  inspected evidence may remain with an explicit retained-state report.

New generalized plan format should not silently alter old schema1/schema2 hash
inputs. Add independently versioned disposition/source/owner transition records
or a new schema with a demonstrated old-native compatibility boundary. Bind exact
B/I/C payloads, original/native generations, selected path choices, final materialized
candidate and its metadata, all affected exposures, owner before/desired entry
images, external preferences and the review fingerprint. Revalidate at apply.

## Source and ownership transitions

Source changes are explicit. Preserve upstream author versus republisher versus
local fork versus native distributor. Stage selected source into private state
with actual gh capabilities wherever applicable; verify exact Git tree/blob IDs or
complete local snapshot before any consumer switch. --upstream is a source switch,
not a routine update flag. Local imports must preserve authored bytes and record
the actual observed source path. Restore recorded/current, move to approved latest,
and unpin remain distinct requested purposes.

An owner adapter must declare supported schema/context and native lifecycle; don't
write another manager's metadata using the gh editor. Preserve unrelated raw entry
bytes/extensions and fresh independent changes. Unsupported or ambiguous owners
stay diagnosed with concrete action guidance until an explicit native transition
adapter is available; that is a pending original obligation, not acceptance.

Combine payload, owner and external-policy state in a durable ordered journal:
validate/retain original; promote verified candidate; publish corresponding owner
and desired state; verify all; acknowledge completion. Enumerate every crash point
and a recovery/undo action using retained native objects and exact review hashes.
Never infer success from process exit alone. Keep session reload unknown unless
actually observed. A journal must describe partially updated owner/payload state
and refuse concurrent foreign mutations rather than blindly restore an old whole
registry. Partial failures retain already completed changes and both streams.

## Proof matrix

Changed tracked file, extra file, local deletion, mode/link/empty-directory change,
unchanged upstream tree, same-name different-origin/context, pin and explicit unpin,
shared target and per-consumer fork; nonoverlap merge, same-path and ancestor/type
conflict, source switch and local reimport. Verify review-fingerprint staleness,
disk/cancellation/process-kill at each journal boundary, concurrent owner mutation,
restart recovery, repeat no-op and undo restoring exact originals/extras/exclusions.
Test actual gh minimum/current behavior, preserving observed help/runtime mismatch.
All target platforms/native hosts plus independent review and original-criterion
closure audit are required. M08 remains open until this entire acceptance is proven.

Independent review refinements to make concrete:
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

- A source switch must separately choose preserve/drop/replacement pin policy; an
  old pin string cannot be silently applied to a different repository. A fork's
  permanent source must be durable user-owned source storage, never ephemeral
  candidate or retained-plan directories. A manual-fork preference does not by
  itself detach native ownership or make direct gh updates honor that policy.
- Cross-store apply/recover/undo needs an explicit before/desired/foreign matrix
  for payload, native owner and preferences. Undo only the policy generation
  changed by this plan; preserve later independent set/clear actions. Reserve
  publication and undo capacity before displacement and document lock ordering.
- Small coherent batches after suppression: complete reviewed three-way local
  dispositions for current owner contexts; concrete durable fork/source switch/
  local import/unpin-only plus required detach; native owner adapters/migration;
  then unchanged original M08/M09 acceptance audit. Every batch delivers an
  executable workflow and is reviewed, verified and merged before the next.
