# Skill maintenance implementation and evidence

The 2026-10-13 audit contains M01–M21. Implementation proceeds through separate
PRs. Each batch needs local tests, disposable runtime proof, independent review,
resolution of accepted findings, re-review, required CI, and merge before the next.
The source audit is `skillview-maintenance-research/backlog.json` attached to the
originating Codex chat. No installed user skills are test targets.

## Batches

1. Inventory facts and removal classification: M01, M02, M04; protective portion
   of M03 and foundations for M11/M12. Merged as PR #42, a0721245.
2. Exact updates and complete results: M05–M07 and protective portions of M08/M09.
   Merged as PR #43, b4b2c80e, after final CI38521164480 passed all 904 tests and AOT targets.
   Local changes refuse replacement; explicit dispositions, durable backups and
   recovery complete M08 in batch 4.
3. Sources, contextual lock evidence and bounded payload comparison: M03/M09/M11
   comparison foundations and protective ownership rules. Full repair/migration
   actions complete those findings in subsequent batches.
3b. Discovery/capability catalog, namespaces and ancestor contexts: M10.
4. Recoverable repair plans, relinking and consolidation: M13–M15 and remaining M08.
5. Native owners and agent visibility: M12 and M16.
6. Desired state, adoption, skill diagnostics and maintenance UI/CLI: M17–M20.
7. M21 contract/adversarial verification accompanies every batch, followed by a
   final acceptance audit against every original criterion.

## Batch 3 behavior and remaining scope

- Source identity, physical address, agent exposure and revision evidence are
  separate facts in list JSON schema 3. New offline `compare --path …
  --other-path … --json` inspects the entire bounded payload and contextual locks.
  It distinguishes source, revision, context and payload relationships, reports
  scripts/resources/mode differences and never selects a survivor by age or path.
- User `.agents/.skill-lock.json`, XDG state lockfiles and project `skills-lock.json`
  retain separate contextual records, schema versions, source paths, refs, pins,
  hash domains, distributor versions and whole-record fingerprints. Ancestor
  project locks cover only documented project installation directories. Every
  claim and unresolved coverage diagnostic survives reconciliation and protects
  mutations. A corroborated shared gh/Vercel record is not an exclusive owner.
- gh's narrower typed lock writer can discard extensions or reset malformed
  schemas. Install/update adapters refuse those writes. Same-selector reinstalls
  refuse even with force; actual namespaced keys are resolved before preflight,
  while unresolved paths, ambiguity and truncated discovery remain refusals.
- Project/custom updates isolate incidental global writes; the user's shared
  registry remains byte-identical. Shared updates retain their original registry
  context and check its source/path/tree/pin immediately before execution.
- These checks do not claim an atomic transaction against an external writer.
  M15 must add coordinated preservation, recovery and journaled record changes.
  Owner-aware uninstall, colliding imports, explicit-path resolution, repair and
  exposure-preserving consolidation remain subsequent maintenance-plan work.
- Discovery is a separate next batch. Native owner catalogs/effective loading,
  desired state, adoption, lint/evaluation and maintenance TUI flows remain on the
  original backlog; this batch does not mark those capabilities complete.

## Batch 1 evidence

- Baseline: main d6a5785 (1.0.1), pinned SDK 10.0.402.
- New YAML dependency 18.1.0 is used through bounded parsing events only; no
  reflection-based object serializer or alias expansion.
- Final full suite: 867 passed, zero failed/skipped.
- Native runtime fixtures under gh 2.97.0 and 2.102.0 verified pins, source/ref
  preservation, custom-root reconciliation, and unchanged payloads after refused
  `cleanup --apply --yes`. See `agent_docs/evidence/maintenance-batch-1.json`.
- Actual gh 2.102.0 pinned install to a disposable directory round-tripped source,
  pin and tree metadata through the scanner and explicit directory inventory.
- Standalone native AOT publish with IL2026/IL3050/IL3053 promoted to errors passed.
- Independent Astra review identified six issues: ref normalization, provider
  coverage, YAML scalar/container types, conflicting names, CLI-only repository
  refs, and cleanup JSON review-only output. Accepted and addressed; re-review
  passed. A second review identified missing partial-scan UI/JSON diagnostics
  and default-only source accounting; both were fixed and a third review found
  no remaining actionable findings.
- First PR CI found whitespace-format errors in two files. Applied the exact
  solution formatter and added its verification command to the agent workflow;
  the formatting-only follow-up is independently reviewed before merge.

## Durable rules

- A name collision or missing catalog observation is a review task, not proof a
  payload can be deleted. Review-only candidates must fail closed in both CLI
  and TUI bulk-removal entrypoints.
- Default gh discovery and filesystem discovery have different coverage. Only a
  successful explicit root observation establishes absence in that root.
- Keep authored versions separate from gh repository refs. gh list shortens
  `refs/heads/` and `refs/tags/`; normalize comparison, retain original metadata.
- Every provider outcome and every discovery context must survive reconciliation.
- Filtered test flags can select fewer tests than expected: use the full suite
  and assert counts, plus exact query filters for opt-in contract methods.

- Windows CI exposed stale directory validation after a child link replacement.
  Parent ChangeTime alone does not reliably detect this. Windows validation now
  brackets policy inspection with bounded, handle-relative immediate-entry
  fingerprints, includes reparse data and rechecks before any deletion callback.
  Entry-limit/read errors refuse removal; cancellation precedes native reads.
  This detects immediate-entry changes, not an atomic recursive snapshot.
  Regression coverage includes in-place retargeting with unchanged link identity,
  refreshed parent identity, capture mutation, cancellation and entry-limit refusal.
- Windows CI run 38511388030 passed all 867 tests after the safety fix. The
  new link regressions require actual Windows symlink support rather than
  silently returning when unavailable.
- Final batch 1 CI run 38511755536 passed every platform/AOT check. PR #42
  merged at a0721245d0681e43688fdc463d4521b78d2a3857.

## Batch 2 evidence

- Product update paths retain selected physical records and use a freshly verified
  containing directory plus basename selector. Confirmation passes gh `--all`
  without expanding the selected physical set. Ambiguous CLI names refuse;
  `--path` and `--install-id` select exact copies.
- Native AOT CLI fixtures under gh 2.97.0 and 2.102.0 exercised an actual available
  update from cli/cli commit 33789149b9def6b66ddf247438a79fac94e22074. The selected
  ID changed, the other copy stayed byte/mode identical, and `--force --unpin`
  refused a local extra without attempting mutation. Both streams survive JSON.
- Opt-in contracts also reconstruct the recorded immutable tree when the installed
  branch ref has moved. A ref whose payload changed cannot masquerade as the base.
- Complete bounded payload fingerprints cover every file, directory, Unix mode
  and link leaf. Installer-owned metadata is separately normalized; authored
  content and comments remain comparison evidence. Native reads remain relative
  to held roots; completion rechecks entries, ancestors and Windows directory
  manifests. This is change detection, not an atomic filesystem snapshot.
- Cancellation kills/drains the child and preserves captured streams; every
  attempted mutation gets independent bounded verification and inventory invalidation.
  Partial inventory never erases per-target evidence.
- Independent reviews found and corrected targeting, cancellation, metadata
  normalization, moving-ref, staging cleanup, late-edit, malformed-provider,
  partial reporting and contextual ownership issues. Final source review is clean.
- Native CLI proof exposed global lock ownership leaking by name into custom
  copies. Production now retains all contextual claims and associates each by
  physical/exposure containment. Competing claims remain conflicts. Additional
  lock schemas/locations are deliberately left for batch 3.
- Tests and reproducible runtime evidence are recorded in
  `agent_docs/evidence/maintenance-batch-2.json`; the proof script accepts explicit
  app/gh paths and uses disposable roots. Windows native execution is required CI.
- First batch 2 CI passed macOS/Linux tests and every AOT publish but found a
  Windows same-length rewrite with unchanged native generation. Completion now
  rehashes every file through held-root-relative opens on every platform, bounded
  by its originally observed byte count. A Windows regression restores ChangeTime,
  proves unchanged full identity, and requires a content-change refusal. Local
  full suite after this fix: 900 passed; independent re-review clean.
- CI run 38519371372 executed the Windows identity-preserving regressions and
  passed all 900 tests and every platform/AOT check after the rehash fix.
- Source inspection confirmed gh `--dir` installs also write the shared
  `~/.agents/.skill-lock.json`. Comparison staging and mutating contract fixtures
  now use per-child private HOME/USERPROFILE and XDG state; original gh auth config
  resolution is preserved with gh's documented precedence, including Windows
  `APPDATA/GitHub CLI`. Parent environment stays unchanged. Native proof verifies
  the shared lock is byte-identical after a local-drift refusal on both gh versions.
- The current full local suite passes 904 tests. Shared gh/Vercel schema-3 records
  are interoperability evidence, not proof of exclusive native package ownership;
  resolving that distinction remains M09 in batch 3. This batch conservatively
  refuses updates when such a contextual claim is present.

## Batch 3 verification

- Independent Astra reviews and re-reviews found no remaining actionable source
  findings after the accepted fixes listed in `evidence/maintenance-batch-3.json`.
- Warning-free build, all 951 tests, solution formatting and site build passed.
  Final App native AOT publish promotes IL2026/IL3050/IL3053 to errors.
- Actual gh2.97.0 and2.102.0 fixtures prove shared exact updates, preservation of
  unselected copies, byte-identical shared registry after custom updates, refusal
  of conflicting/unsupported writes, whole-payload extras and contextual revision
  differences. Final offline traces contain only `gh skill list`; no auth, remote
  API, installer/updater or telemetry child was launched during comparison.
- Platform CI, including Windows tests and every AOT RID, is required before merge.
- A fresh NuGet/project-file comparison confirms all dependencies are current
  stable: Terminal.Gui2.5.0, Editor2.5.7, YamlDotNet18.1.0, test SDK18.11.0,
  xunit.v3 4.0.2 and VS runner4.0.1. No further dependency update is needed.

- Copilot findings and subsequent independent reviews are addressed: install
  admission includes project/XDG records and retained discovery-time evidence;
  aliases and relative child environment roots cannot bypass owner detection.
  JSON exposes every owner pin/version, covered root, and unmatched record.
- Shared update fingerprints survive unrelated gh rewrites, including missing
  versus empty known string fields. Actual two-target forced updates pass on
  gh 2.97.0 and 2.102.0 after removing optional fixture timestamps.
- Native fixtures separately prove project/XDG overwrite refusal, project-alias
  owner discovery and whole-registry preservation. Broken-link and empty-directory
  alias protections have regression tests. Repository descriptions can contain
  unescaped newlines; updates stage the recorded source path and inspect the
  materialized payload instead of guessing a selector from those descriptions.

## Batch 3 merge

PR [44](https://github.com/harder/gh-skillview/pull/44) merged as
`a50e7b2e3738b1d057398f6654043cfb6cb66362`. Reviewed head
`2f23a1dce632e98c7a54d65ab158b0c611fc1d05` passed all 951 tests on
Linux, macOS and Windows, four AOT targets, and CodeQL (CI 38587538990,
CodeQL 38587539210). Native JSON also preserves an unmatched owner's pin,
distributor version and covered paths with the documented no-matches exit 20.

## Batch 3b discovery

M10 discovery foundations now share gh's 48-agent placement catalog across
filesystem scanning, installation and Doctor detection. Native/legacy Copilot,
Gemini and OpenCode project roots remain explicit observation conventions;
Codex and Amp retain separate native config detection. Shared directories do
not identify which individual agents are installed or enabled.

The scanner walks bounded namespace/package containers and stops at skill
payloads. It rejects child container links, special metadata files, and unlimited
prefix reads. Bounds cover aggregate entries, enumerated directories, namespace
depth and metadata bytes; partial reads before I/O failures still consume the
byte budget. Omitted coverage cannot produce orphan cleanup recommendations.

Global `--workspace` flags add an explicit workspace collection. Named
`--profile agent[:name]=skills-directory` flags preserve alternative profile
exposures without inferring enabled state. Physical CWD/worktree ancestor
observations coexist with gh's actual Git context under its effective child
environment. Relative HOME/config/state paths use that same captured CWD.
Windows and Unix alias keys merge physical observations while retaining their
original display/removal-policy paths.

Native manager manifests, bundled resources and effective loading policies
remain in M12/M16; this batch does not infer those from placement alone. The
recoverable repair/journal, desired-state and maintenance UI batches remain.

Batch 3b local verification: warning-free build, 975 tests passed, solution
format verification, and sequential standalone/extension native AOT publishes.
Disposable runtime fixtures pass on gh 2.97.0 and 2.102.0, including redirected
Git workspaces, profiles, namespaces, aliases and partial-coverage cleanup
refusals. The preceding shared/exact update runtime proof also passed on this
batch host. Platform CI and final independent review are required before merge.

First batch 3b CI passed all platform/AOT checks except a Windows dangling-link
cleanup regression. Directory.Exists can report the link itself as present on
Windows, so linked-container classification now requires a resolving target.
A new all-platform test distinguishes dangling links from live containers; the
existing container test also runs on Windows. Final native fixtures prove
identity-pinned dangling-link cleanup on both supported gh hosts. Fresh CI is
required for the corrected head.

All four subsequent Copilot findings are addressed: metadata inspection has its
own verified-ABI capability, hidden directory/link omissions make coverage
partial independently of location-table bounds, startup uses the coordinator
capture path, and Git initialization/discovery share an isolated context. Final
independent re-review passed 115 focused tests. Linux AOT CI now denies openat2
with EPERM and ENOSYS in fresh processes, requiring valid metadata and owner
lock observations alongside unchanged payloads after a native removal refusal.
The private gh protocol fixture tests capability separation, not gh semantics.

## Batch 3b merge

PR [45](https://github.com/harder/gh-skillview/pull/45) merged as
`34844d51606fbfc9522f2ebcd10e780aee32bf1f`. Reviewed head
`fd16ce695b2a2288ab9304b2be9dc8d3e4aaf373` passed all 975 tests with
zero skips on Linux, macOS and Windows, every AOT target and CodeQL
(CI 38600598555, CodeQL 38600598577). Linux native proof verified both
EPERM and ENOSYS openat2 denial scenarios. All four Copilot threads were
addressed and resolved after independent re-review.

## Batch 4a: private durable payload evidence

The first M08/M15 recovery foundation adds exact `backup --path`/`--install-id`
CLI selection and a bounded archive plus append-only creating/captured records.
State stays outside known payloads/agent roots, uses private native authority,
and coordinates SkillView writers. Source bytes/modes, empty directories and
native link leaf bytes are verified; damaged markdown can still be captured.
Incomplete/canceled evidence remains available without pruning prior records.

This does not complete M08/M15: the schema does not yet restore ACLs, extended
attributes, root permissions or package-owner registries, and does not authorize
in-place restore or update replacement. Dispositions, verified recovery plans,
repair/relink/consolidation, native owners, effective loading and the remaining
policy/UI findings continue in subsequent merged batches.

Independent Astra review/fix cycles corrected native authority, renamed exclusion
objects, privacy, durability, Darwin ACL and ARM64 varargs handling, callback
handle disposal, cancellation, ancestor refresh and inherited writer locks.
A separate reproduction proved a child could retain a dup'd lock; the final
borrowed CLOEXEC handle and regression acquire the next writer while that child
is still alive. Final focused review has no actionable findings.

Local final verification: warning-free SDK10.0.402 build, 1005 tests passed with
zero skips, solution format verification, site build, sequential native App and
extension publishes. Both native hosts pass the disposable backup proof with
gh2.97.0 and2.102.0: exact IDs, archive/journal hashes, complete bytes/empty dirs,
link leaves, private Unix modes, unchanged selected/unselected copies and the
real shared registry. Native proof runs in all four AOT CI targets. Linux and
Windows execution plus required CI are pending before merge. Reproduction,
binary hashes, accepted findings and primary OS sources are in
`agent_docs/evidence/maintenance-batch-4a.json`.

First batch4a CI passed macOS tests and Linux/macOS native proofs but exposed a
Windows-only test assumption: an exclusive open archive can prevent moving its
parent. The regression now proves both safe outcomes explicitly: native refusal
retains incomplete evidence at the original state address, or successful movement
is detected before captured publication. Both require the attempted boundary,
creating record, retained archive, no captured record and unchanged source.
A separate M21 follow-up will make every legacy Unix native descriptor duplication
CLOEXEC atomically; the backup writer's borrowed handle is already protected.

Copilot's three subsequent findings are accepted and fixed. Inventory now retains
bounded configured placements before existence filtering with a distinct
configuration completeness flag. Backup excludes expected missing roots and
refuses omitted/unresolved configuration, including private aliases and named
profiles. Raw evidence bypasses all markdown parsing/normalization and preserves
invalid UTF-8 and large markdown within its aggregate budget. The Windows payload
walker opens its root without DELETE, while removal defaults retain that right.
The ACL regression proves a readable non-deletable source is backed up unchanged.
Independent re-review passed 55 focused tests; fresh full verification passed
1018 tests, zero failures/skips. The prior test-only head 44b6045 passed every
platform/AOT check (CI38614178840, CodeQL38614178677); the new production fixes
require fresh native proofs and CI before merge.

The first production-fix CI head d6e56c0 passed all native proofs, Linux/macOS
1018-test suites and CodeQL, but Windows fixture cleanup retained read-only
inherited ACLs after a successful backup test. The fixture now rehydrates saved
DACL binary forms into fresh security objects (marking them modified), restores
parent/payload/file ACLs explicitly, and preserves the original source owner.
This is a test cleanup fix; no backup production permission policy is changed.

Final batch4a CI38619873846 and CodeQL38619873873 passed at reviewed head
303b1968: 1018 tests on Linux/macOS/Windows and both-host native backup proof
on all four AOT targets. All three Copilot findings were fixed and independently
re-reviewed. PR #46 merged at 4c4e5c68de1985d83db3e1f5d3a01481087f5900.

## Batch 4a follow-up: Unix descriptor inheritance (M21)

Legacy plain `dup` cleared CLOEXEC on payload streams and introduced an
inheritance interval before directory enumeration. Every duplicate now uses
atomic `F_DUPFD_CLOEXEC`, with verified Linux and Darwin ARM64 bindings and no
non-atomic fallback. Existing bounded EINTR retries remain. Unverified ABIs refuse.

The regression checks flags immediately after both file and directory duplication,
before stream/fdopendir admission. A real `/bin/sh` exec cannot read the held
payload descriptor, while the parent stream remains readable and the completed
snapshot and original bytes remain unchanged. Replacing only the helper with
the previous implementation made three of four cases fail: both duplicates lacked
CLOEXEC and the child successfully read the private fixture. This establishes the
regression detects the observed defect. Darwin fdopendir itself sets CLOEXEC;
the defect is the initial duplication interval and the payload stream, not a
claim that completed Darwin directory streams remain inheritable.

See `evidence/maintenance-batch-4a-descriptors.json` for verification. The remaining
M08/M13–M15 recovery and repair work follows after this batch merges.

Batch4a descriptor follow-up merged as PR #47 at f6b34db2 after independently
reviewed head1bc0e2d passed CI38621616883 and CodeQL38621616839: 1022 tests on
all three test platforms and both-host backup proofs on all four native AOT RIDs.
Copilot reported zero findings.

## Batch 4b: verify retained history before recovery

`history [--id BACKUP_ID] [--json]` inspects existing private state without
creating entries or changing installed skills. A held native reader lock
serializes it with cooperating capture writers. Complete records require matching
creating/captured journals, raw archive hash, strict schema, bounded ZIP layout,
every blob hash and size, complete directory relationships and payload fingerprint.
Interrupted, orphaned, corrupt and unrecognized records remain visible; one unsafe
leaf is never followed and cannot hide valid peers. Unsafe store/lock privacy,
unavailable state, traversal errors or whole-store quota refusal remain explicit
environment failures.

The reader accepts only capture's uncompressed, single-disk, comment-free ZIP
format. It validates the end record and every central header before constructing
ZipArchive. Each archive uses one exact-sized private byte snapshot bounded by
MaxArchiveBytes (282 MiB) so validation and parsing cannot observe different ZIP
structures. The original held file is rehashed before verified publication.
Manifest JSON is bounded to16MiB/depth16, payloads to10000 entries,256MiB regular
file bytes and32 directory levels, state to1GiB/4096 entries. Cancellation is
checked between reads, entries and directory ancestors. A separate1GiB aggregate
manifest/blob verification budget spans every record, charges before reading and
retains charges after failures; exhaustion explicitly refuses inspection. This
also bounds work when different ZIP entries reference the same stored bytes.

Verification means byte consistency of raw payload evidence. It is not
authentication against a same-user writer, ACL/xattr/root-mode restoration, or
in-place replacement authority. M08/M13-M15 recovery, dispositions, relinking
and consolidation still require subsequent plan/apply work.

Initial PR #48 head e4dadfd passed CI38627565766/CodeQL38627565816 and all
four native targets. Copilot then found a valid Unix whitespace-only payload
name incorrectly rejected by the reader's generic required-string helper. The
old native host reproduced this as a corrupt result for intact evidence. Payload
path admission now preserves whitespace while keeping structural checks; two
round-trip regressions and both native-host proofs cover whitespace files,
directories and children. Independent re-review is clear; full suite1055 passed
with zero failures/skips. Fresh CI is required for this production fix.

Final PR #48 head4270510 passed CI38629955734 and CodeQL38629955648:1055 tests
on every test platform and both-host native history proofs on all four AOT RIDs.
The Copilot whitespace finding was fixed, independently re-reviewed and resolved.
Merged at5a0db7c3f93d5459fb62ab377bd9734d2684eb94.

## Batch 4c: protect Git worktree metadata before recovery mutations

The prior native product permitted `remove --yes` on an actual disposable linked
worktree: its `.git` file was not recognized by the directory-only guard, and a
parent-repository warning could be accepted. The baseline proof deleted three
tracked files and the checkout directory; no user repository was a test target.

Native policy now observes any `.git` entry relative to the held directory
without following it, including gitfiles and broken symbolic links. Only true
absence is accepted; observation failures refuse. Managed non-mutating previews
apply the same marker policy. The legacy ContainsGitDirectory error kind and
HasGitDirectory field retain compatibility; their protection includes all Git
metadata entry types. The removal wizard describes repository/worktree protection.

The native proof creates a real private Git repository and linked worktree with
private Git configuration/templates/hooks, then requires explicit execution
refusal, zero deleted entries, unchanged tracked files/gitfile and a still-clean
usable checkout. A broken Git link is protected without accessing its target.
Both hosts and all native AOT targets run the proof in CI. Git's documented
gitfile layout: https://git-scm.com/docs/gitrepository-layout.

This closes an M03/M21 protection gap. Quarantine/undo and the remaining
maintenance acceptance criteria continue after this batch merges.

Local verification: SDK10.0.402 build clean,1061 full tests passed, formatting and
site checks passed. Both native hosts passed the real-worktree/broken-link proof
with gh2.97.0 and2.102.0, plus backup/history regression proofs. Independent review
and re-review are clear;72 focused tests independently passed. Fresh platform CI
is required before merge. See `evidence/maintenance-batch-4c.json`.

Final PR #49 head1b326aa passed CI38633124149 and CodeQL38633124170:1061 tests
on all three test platforms and both-host worktree proofs on all four AOT targets.
Copilot reported zero findings. Merged atcf578f7971a4be69cd2fe4c45b813fb4f063d373.

## Batch 4d: separate native continuity from payload equality

A raw payload hash can remain equal after a same-content file replacement, and
the schema-1 archive hash omits the root Unix mode. Bounded native inspection now
retains the root and every entry's full object ID and generation, including both
Windows file-ID halves and CreationTime/ChangeTime, plus observed Unix modes.
A separate object-state fingerprint is deterministic across read-only captures.
It is distinct for separate copies and changes when observed objects/generations
change. The existing native walk's held-parent, leaf-link, cancellation, content
and completion revalidation contracts remain in effect.

Backup publication requires unchanged raw payload, address and native object
evidence between inspection and archive traversal. Same-content replacement and
root-mode changes leave an incomplete creating journal rather than captured
evidence. Archive/history schema1 remains unchanged; no extraction or new restore
authority is introduced. Compare JSON exposes native fingerprints and root modes,
and differing root modes prevent consolidation candidacy.

This evidence does not attest every ACL/xattr/alternate stream or external hardlink
relationship. Moving an original directory can preserve live metadata, but
already-open handles and hardlinks can still edit it. A private Windows parent
also does not necessarily make retained broad-ACL descendants private. Recovery
plans must distinguish preservation from immutability and explicitly bound their
metadata/topology admission before apply or undo. Quarantine/undo and the remaining
M08/M13-M15 acceptance work continue after this batch.

Local verification:1069 full tests passed, zero failures/skips, clean build/format/
site checks. Independent source review/re-review is clear and61 affected tests
independently passed. Both native hosts passed object-evidence proofs with
gh2.97.0/2.102.0 and backup/history regression proofs. Fresh platform CI remains
required before merge. See `evidence/maintenance-batch-4d.json`.

## Batch 4e: finish the standalone quarantine/undo milestone

The user narrowed this round to one usable quarantine/undo workflow, then stop.
PR #50 merged at af80737d3a025fb37fbcae086b8b2626be7b555f after CI38635536531,
CodeQL38635536377 and 1069 tests on all three test platforms. Its object-evidence
proof passed for both hosts on all four native targets; Copilot had no findings.

This batch adds CLI preview, fingerprint-bound explicit quarantine, undo preview,
undo and recorded history for a standalone custom-directory copy. It preserves
the live directory with a same-volume exclusive native rename and durable
prepared/completion journals. No archive extraction, replacement, recursive
copy/delete fallback or manager-record edits occur. Root/parent generations,
object continuity, payload evidence, known ownership, configured roots and
original-slot vacancy gate execution. Native policy and repeated held-directory
inspection are tested, including the fresh Unix enumeration cursor requirement.

The workflow and its limits are documented in `../docs/quarantine-and-undo.md`.
Interrupted or ambiguous operations remain inspectable but are not automatically
recovered; even a pre-rename cancellation after undo preparation requires
inspection. This is relocation rather than security isolation, and agent session
state remains unknown. The proof uses only disposable custom skill fixtures.

This finishes the agreed maintenance milestone. Broader M08/M13–M15 owner-aware
repair/consolidation and generic plan/apply, remaining agent UX/doctor work and
complete metadata attestation remain follow-ups. They are not represented as
implemented by this batch. The original macOS native crash has not been reproduced.

Copilot review found two consumer gaps: dangling links activated by undo and
separately discovered subtree skills. Both now gate relocation/undo before journal
admission using bounded intended-address resolution and canonical overlap checks.
Six regressions and native refusal proofs cover the fixes, including ambiguous
alias-parent traversal refusal. Independent re-review validates the final fixes.

A second Copilot pass identified a nested-root outward link: its destination
was outside the move while its entry was inside. Consumer admission now checks
both the canonical parent/final-name entry address and intended destination,
with an isolated regression preserving the external payload and original link.

The final independent policy review also protects configured/scanned roots within
either relocation boundary, even empty or expected absent, including root aliases.
Three fixtures and a native empty-root refusal proof cover this case.
