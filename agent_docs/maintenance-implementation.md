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

Entry-address checks strip a trailing directory separator before identifying the
final entry, preserving an outward root link as a link entry. A trailing-root
regression and native refusal proof cover that spelling.

## Batch 5: saved recorded-revision replacement and restart recovery

The full original 21-finding objective is active again. The immutable audit is
now checked in as `maintenance-audit.json`, with a separate acceptance ledger in
`maintenance-acceptance.json`. Protective foundations alone do not complete an
item; missing capabilities and criterion-by-criterion proof remain explicit.

This batch advances M08/M13/M15/M20/M21 with usable CLI recorded restoration.
Plans stage the exact recorded Git tree through authenticated gh APIs, verify
Git tree/blob identities, preserve authored markdown bytes while rebinding only
installer tracking, and save canonical JSON fingerprints plus full manifests.
Apply retains the original live directory and promotes the reviewed stage via
exclusive native moves. Explicit fingerprint-bound inspection/recovery handles
interrupted moves; undo retains the replacement before returning the original.
Source/tree/pin and contextual owner entries remain unchanged.

Native staged directory/file creation is relative to held parent authorities.
No path truncation is used. Unix file and bottom-up directory barriers precede
ready-plan publication; Windows creation uses write-through. Consumer checks
rescan configured roots that newly exist, and live owner reads revalidate both
captured claims and prior absence. Complete remaining transitions reserve journal
capacity, with a separate bounded undo journal when forward records are exhausted.
Partial journal files remain untouched and non-authoritative.

Independent review/re-review addressed BOM loss, partial-journal recovery, new
consumer/owner detection, root mode review, missing-root discovery, journal
capacity, namespace durability, path-write authority, malformed YAML, subtree
ordering and whitespace-named local extras. The native real-gh proof covers both
gh2.97.0 and2.102.0, direct and shared pinned copies, same-name isolation, exact
staged/native original identity, raw registry preservation and a killed CLI
followed by separate-process inspect/recover/undo. See
`evidence/prove-recorded-replacement.py` and `docs/recorded-restoration.md`.

This batch does not complete all of M08 or M15: newer revision/owner transitions,
local forks/dispositions, suppression state, native package adapters, generalized
plans and the unified TUI maintenance queue remain in the original acceptance
ledger. Full platform CI and the final 21-item acceptance audit remain required.

Real-machine read-only inventory exposed hidden Codex containers as partial
coverage. Every restore command now accepts `--allow-hidden-dirs`, and fresh
transition scans include hidden entries within existing bounds. Initial Windows
CI found private-ACL and extended-path fixture assumptions plus a real restart
failure: a dangling directory exposure can satisfy `Directory.Exists` while its
target is retained. Native state pinning now accepts only native file/path
absence for that target, retains its exclusion address and propagates other
errors. Shared-consumer tests interrupt both retention and undo displacement.
The full local run also exposed a pre-existing 10 ms timer-test registration
race. Deadline tests now defer arming a real system timer until registration,
using the optional TimeProvider seam inside the existing suppressed context.
Production keeps the system provider; callback failure and context assertions
remain unchanged. Independent review cleared this follow-up.

Copilot found stale tracking before planning and singleton namespace recovery
gaps. Planning now binds parsed current tracking to the held original's raw and
native fingerprints before staging. Transition scans recognize only the saved
native namespace parent without its own skill/Git/observed ownership, preserving
README files and handling stale malformed-parent rows after child restoration.
Owner revalidation includes the physical namespace installation root. Focused
coverage includes source/tree/ref/pin drift, normal and interrupted namespace
apply/undo with and without README, and newly introduced contextual ownership.
The native proof also exercises a nested namespaced installation.

PR #52 merged as `178476f7d8b0c9f7ab6cb5dcf4066410f0570c7c` after CI
38679954928 and CodeQL 38679954897 passed. The merged tree exactly matches the
reviewed source tree. Final local and three-platform suites pass 1178 tests;
both native hosts pass the real-gh proof on all four RIDs. Local AOT hosts also
pass gh 2.97.0 and 2.102.0 with direct/shared/namespaced fixtures and real process
kill/restart recovery. Both Copilot findings are resolved after independent
re-review. See the finalized `evidence/maintenance-batch-5.json`.

## Batch 6 checkpoint: immutable newer revisions and preserving registry sessions

The active batch now provides ownerless saved updates through `update-plan`,
`update-inspect`, `update-apply`, `update-recover` and `update-undo`. Schema 2 binds
the resolved commit/source tree, original tracking, desired tracking/unpin,
recorded base and local differences. Schema 1 restore fingerprints remain
compatible. Applying promotes the reviewed native candidate without another
source resolution and retains the original directory for verified undo.

The existing-only native registry lease matches gh's Unix/Windows data-file
lock, pins the parent/address/file and writes through the same file object.
The preserving editor can replace one exact selected entry while retaining
fresh unrelated records and extensions. The standalone write session durably
records raw before/desired evidence before truncating, attempts bounded live
preimage restoration after write/cancellation failure under the same lock, and
retains unattributable foreign bytes for explicit review. It is not yet wired
into shared payload replacement; those updates remain refused.

Independent review/re-review fixed legacy source-path/no-tag compatibility,
recorded-base directory admission and incomplete baseline-stage diagnostics.
Local verification passes 1,264 default-suite tests, style, locked restore and
site build. Both macOS native AOT hosts publish, with standalone AOT warnings
promoted to errors. Actual gh 2.97.0 and 2.102.0 processes respect the held data
lock. Twelve disposable newer-revision CLI fixtures (four debug, eight AOT)
prove direct/nested namespace updates, explicit unpin, local-change review,
same-name isolation, unchanged registry bytes, native object continuity and
real process-kill/restart/recovery/undo. See
`evidence/maintenance-batch-6-checkpoint.json` and
`evidence/prove-revision-update.py`.

Batch 6 is not ready to merge: shared owner integration must retain the data
lock across payload and registry transitions, use same-handle registry reads,
bind phase-aware owner evidence and offer explicit torn-write review after
process death. Ordinary product updates/TUI routing and the full platform proof
remain. The original 21-item objective is unchanged; this checkpoint does not
close any item's final acceptance.

## Batch 6 checkpoint: shared owner transitions and reviewed torn-image repair

Saved newer-revision updates now coordinate one verified global gh owner with
the retained/promoted payload. A held native registry lease spans every move,
same-handle contextual reread, selected-entry write and terminal journal step.
The selected entry preserves extensions and installedAt; apply and undo rebase
into fresh unrelated manager bytes. Owner state and full raw/native registry
snapshots participate in recovery review fingerprints. Owner journals reserve
separate bounded undo capacity, including when forward evidence is incomplete.

`update-repair-owner --yes --expect` repairs only explicitly reviewed torn JSON
from applicable unfinished causal evidence. It never moves payloads. Valid
manager JSON uses selected-entry patching, while foreign/unsupported content and
unrelated inventory failures remain refusals. Latest durable write IDs, exact
images, bounded ancestry and terminal evidence prevent arbitrary historical
preimage replay. Partial main/raw intent publication, live compensation and
interrupted repair remain recoverable only under the verified evidence contract.

Independent review/re-review fixed initial journal creation, reserved undo
accounting, missing/partial main/raw intent windows and proof-host containment
and candidate assertions. The baseline full suite passed 1,290 tests, followed
by 124 focused cases covering the final refinements (including 28 shared owner
cases). Both macOS AOT hosts publish; strict standalone AOT diagnostics pass.
The strengthened disposable proof kills a separate process at production
checkpoints, then uses the actual product CLI for inspection, explicit image
repair, recovery and undo. It compares the installed candidate independently and
proves original native payload/exposure continuity and unrelated preservation.
See `evidence/maintenance-batch-6-shared-owner-checkpoint.json` for exact runtime
results and hashes. Four-RID CI now runs both saved-update proof scripts and
retains JSON evidence artifacts.

The batch remains in progress. Ordinary CLI/TUI routing still needs the reviewed
per-target queue described in `maintenance-update-plans.md`; sibling plans
prepared together cannot silently bypass generation changes. Final workflow
review, exact-source full/platform/native checks and PR merge remain. All 21
original findings retain their final acceptance obligations.

## Batch 6 refinement: registry namespace witness

Saved shared updates now retain the held registry parent's full native identity
and generation across planning and execution leases. This refuses namespace
replacement/reuse even when the registry file ID and selected bytes match,
while ordinary in-place manager writes remain supported. Validated private state
ancestors are initialized before the witness so a state home beneath `.agents`
does not invalidate its own first plan; staging never refreshes that witness.

Independent review/re-review found no remaining actionable source issues. The
exact refinement passes all 1,303 default tests, zero-warning/error build, style,
and actual minimum/current gh data-file lock contracts. See
`evidence/maintenance-batch-6-registry-parent-checkpoint.json`. Earlier native
runtime results remain attributed to their prior checkpoint; final native and
platform proof and ordinary CLI/TUI routing remain pending.

## Batch 6: ordinary product review/apply routing

Ordinary CLI `update` now stages one immutable candidate and returns a saved
manifest and exact apply arguments. It does not mutate an installed payload,
including with `--force`. Other selected targets report independent retained,
refused or not-started outcomes. Dry-run remains a disposable availability check
and consumes no plan quota. The TUI captures selected physical targets, prepares
one at a time, displays source/local-change/consumer/mode/pin evidence, and
requires a fresh retain-original checkbox plus Apply reviewed plan for each.
The next target waits for the preceding apply/reconciliation. Recovery references
survive advancing the queue; failed reconciliation cannot hide a verified change.
The global update shortcut opens the workspace without fabricating pending rows.

The full-suite run exposed a real Unix state-writer gap: plain stream close could
leave flock held by a forked/duplicated open-file description. State writer
streams now explicitly unlock before synchronous/asynchronous close, with
idempotent release, initialization cleanup and nonthrowing finalizer behavior.
Six deterministic cases retain duplicated descriptors, including repeated old
owner disposal and already-closed finalizer cleanup. Windows-specific test and
proof failures were exact exception typing and CRLF fixture normalization; fixes
preserve no-create assertions and exact registry byte equality.

Separate reviewers re-reviewed all agreed findings. Both native macOS AOT hosts
publish; real minimum/current gh shared process-kill proofs and ownerless native
proofs pass. Actual PTY `u`, marked `U` and Update-button proofs verify unchanged
payload/registry before approval, exact promoted candidates, retained original
native objects, successful quit, CLI inspection/undo, exact registry restoration
and an unchanged unselected installation. The all checkbox cannot widen row or
marked shortcuts. See `evidence/maintenance-batch-6-product-routing-checkpoint.json`.
The final local suite passes all 1,327 tests with zero failures. The duplicate-
descriptor release regressions fail all four cases against pre-fix commit
8527301 and pass with the fix. Final PR-head platform checks/readiness review
remain before merge; the original 21-item acceptance audit is unchanged.

## Batch 6 refinement: cross-platform process-proof admission

PR-head 9102474 passes all three platform test jobs and both Linux/macOS native
jobs. The Windows x64/ARM64 native jobs reached the shared-owner process proof,
then the proof host refused its marker before the first checkpoint: Python text
output translated the required LF marker into CRLF. The fixture now writes exact
bytes while preserving the host's strict disposable-workspace guard. A positive
ordinary HOME/state control must pass that guard before the two linked-directory
negative controls can count as containment evidence. Merged, bounded host output
also makes early exits visible immediately instead of hiding their diagnostics
behind the checkpoint timeout.

This changes the proof harness only. Historical product/source evidence remains
attributed to its original checkpoint. See
`evidence/maintenance-batch-6-proof-admission-refinement.json` for the new local
native proof and independent review; final-head Windows/native CI remains the
merge gate. No original acceptance criterion is closed by a fixture correction.

## Batch 6 refinement: Windows registry namespace evidence

Run 38698483453 verifies the LF proof fix on all four native targets, including
both Windows architectures. Its Windows test job exposed a separate production
gap: parent ChangeTime can remain equal after a new sibling appears. A registry
lease now captures and revalidates a bounded immediate namespace fingerprint
through its held parent handle, opened with enumeration access. It retains full
native entry IDs, creation times, names and types across reopened leases, while
excluding mutable child times/content so registry writes and payload moves below
`skills` remain supported. Double captures bracket the parent's identity/path.

Windows regressions explicitly preserve the parent's timestamps across sibling
creation, removal and replacement; the replaced object remains held outside the
registry parent to prevent native-ID reuse without a delete-pending pathname.
A create/delete generation-only case sets and verifies a distinct timestamp.
The concurrent cross-logger barrier regression now uses dedicated workers so
shared-pool starvation cannot masquerade as lock inversion. Separate reviews
and re-review addressed parent enumeration access and replacement fixture
semantics. See `evidence/maintenance-batch-6-windows-namespace-refinement.json`.

## Batch 6 final merge and acceptance closure

PR #53 merged as `657800503ddb3890560cad2923a617fcbee83207` from reviewed head
`f02c6e15c4bef2d09f0a040d27f27fcaefdcc58e`; their trees are identical.
CI run 38700736316 passes 1,331 tests on each of Linux, macOS and Windows, all
four native AOT targets and the workflow/site/dependency checks. CodeQL run
38700736280 also passes. All four artifacts were downloaded and all 56 proof
JSONs inspected, including 120 fixture rows and 48 actual shared-owner process
kills across both hosts. The complete results are preserved in
`evidence/maintenance-batch-6-final.json`, beyond the CI artifact retention period.

Independent criterion review closes original M05 and M06. It matched all native
and historical PTY evidence hashes and verified that later C# changes affected
Windows execution validation and tests, leaving the attributed macOS TUI routing
proof intact. Exact-target refusal is permitted by M06; unrelated native manager
lifecycle features remain obligations of other findings. The other 19 items
retain their original implementation and final acceptance requirements.

## Batch 7: reconciled inventory filtering

A disposable actual-CLI fixture reproduced incorrect cross-context matching:
user Codex and project Claude exposures of one physical payload also matched
user Claude and project Codex queries. Filters now require one exposure to
match both predicates, while keeping all consumers, aliases, profiles and
metadata conflicts in the selected row. The default gh catalog is captured
before filtering so another agent's observation cannot disappear. Legacy
unscoped memberships retain the installation-scope fallback; agentless custom
rows still support scope-only filtering. The deterministic provider fixture
also changes filters while reusing one complete catalog and asserts the
additional consumer and conflicting sources survive in CLI JSON.

The final local build has zero warnings/errors, style passes and all 1,344 tests
pass (zero failed/skipped). Both native macOS hosts pass the eleven-case matrix
under minimum gh 2.97.0 and current gh 2.102.0, including complete provider
coverage and unchanged native objects/bytes. Independent final source/test/proof
review reports no actionable findings. See
`evidence/maintenance-batch-7-inventory-checkpoint.json`. CI now executes this
proof against both hosts on all four native targets. Final platform evidence
and merge remain required before M02 acceptance closure.

Evidence review and Copilot independently identified a frozen-path-list gap in
the proof. Every unchanged comparison now re-enumerates payload descendants;
positive results require a nonempty 64-hex installation ID. Both hosts pass
the refreshed minimum/current gh matrix, and separate re-review clears the
assertions. Product C# and its already verified 1,344-test baseline are unchanged.

## Batch 7 final merge and M02 acceptance

PR #54 merged as `8029b4f86e85445433d3b142dab3ca65150825c1` from reviewed head `9ac90f3da3cc8e461e07c0e69e618f7e38468dff`;
the trees match. CI run 38708923310 passes all 1,344 tests on each of Linux,
macOS and Windows, all four native targets and workflow/site/dependency checks.
CodeQL run 38708923210 passes. All 64 proof JSONs were downloaded and inspected,
including 88 filter cases and the existing recovery/process-death matrix.
`evidence/maintenance-batch-7-final.json` preserves their full results.

Independent review mapped every original M02 criterion to the current tests and
actual native proofs. M02 is complete alongside M05 and M06; the remaining
18 original items retain their acceptance requirements.

## Batch 8: full-capture inventory outcomes

Ten new CaptureAsync cases exercise credential/network process errors, malformed
and incompatible JSON, explicit truncation, valid empty inventory, successful
default discovery with failed custom-root coverage, denied enumeration and a
missing required custom root. They assert retained filesystem rows, provider and
JSON diagnostics, retry versus cache admission, explicit root coverage and no
absence-based cleanup. Valid empty coverage yields only review-only Untracked.

The baseline passes nine cases and fails only explicit stdout truncation: an
exit-zero process result with valid retained JSON and StdOutTruncated was accepted.
The adapter now refuses that result before parsing, leaving it retryable. The
current real runner already appends a non-JSON truncation marker; this closes
the explicit adapter completeness contract, not an observed runner bypass.
Separate review passes 49 affected tests, including all ten new cases.

The new native POSIX proof uses actual denied directory permissions and a
readable control in private HOME/CWD, checks list/cleanup partial diagnostics,
restores mode in finally and verifies fresh object/byte/mode manifests. CI runs
both hosts on Linux/macOS; the deterministic enumeration-error test runs on all
three test platforms. Final full-suite/native/platform proof and merge remain
required before closing original M04.

The local full suite passes all 1,354 tests (zero failed/skipped), build has zero
warnings/errors and style verification passes. Formatter changes only line
breaks in the new test source; that final source was rebuilt and all ten cases
rerun. Both native macOS hosts publish and pass actual denied-root proofs under
gh 2.97.0 and 2.102.0. `evidence/maintenance-batch-8-provider-checkpoint.json`
preserves exact source/log/binary/proof attribution. Final committed-head
platform proof and merge remain the M04 acceptance gate.


Batch 8 final acceptance: PR #55 merged at `daac6fc5280e7bba6a584d31038a72ea02eaabc2`,
with a tree identical to reviewed head `ece6caa37b0148ad0abfd50dd84496eccb439dfa`.
All 13 checks pass; each platform runs 1,354 tests with zero failures/skips.
Inspected 68 native proof files and 212 fixture rows across all four RIDs,
including four actual POSIX permission-denial proofs. Windows passed a same-head
failed-job rerun after initial GitHub API rate-limit failures. Independent final
evidence review confirms original M04 acceptance. Full results are retained in
`evidence/maintenance-batch-8-final.json`. M02, M04, M05 and M06 are complete.

## Batch 9: actual gh metadata acceptance

Twenty-two deterministic parser/scanner cases cover all literal/folded block
chomping modes, scalar tool patterns, nested authored version/extensions, invalid
and overlong required fields, and the node-count bound independent of character
and depth limits. Four opt-in actual-gh contracts inspect the raw scanner before
gh enrichment, then compare inventory, JSON and Installed detail text with actual
unpinned, pinned and local installs. Read-only checks retain original metadata
bytes. Native proofs compare complete freshly enumerated payload manifests and
registry bytes before/after inventory under isolated HOME/config/state.

The new contracts are included in the minimum/current gh contract workflow;
native round-trip proofs run for both hosts on all four AOT RIDs. Final local,
remote and independent review results remain required before closing M01.

Local batch-9 verification: build has zero warnings/errors, format verification
passes, and all 1,380 tests pass. The 22 deterministic acceptance cases pass.
Separate explicitly enabled contracts pass four cases each under gh 2.97.0 and
2.102.0; the four gated cases in the default suite are not counted as actual-gh
execution. Both sequentially published native macOS hosts pass both gh versions
(16 actual-install fixtures). Source/CI review is clear. Exact attribution lives
in `evidence/maintenance-batch-9-metadata-checkpoint.json`; final remote proof
and merge remain required for M01 closure.

The initial remote contract lanes pass 14 of 15 cases, stopping in the old
fragmented-install fixture because product install admission correctly refuses a
second global-selector overwrite. Fixture setup now invokes native gh directly;
all selected-update, unrelated-copy and local-extra protection assertions remain.
The older mutating fixtures also capture credentials before private HOME and
use private config/CWD/agent paths plus stripped Git overrides. Final source
build/style checks pass; all 15 opt-in contracts pass under both gh versions.
Separate review confirms both fixture corrections. Production code is unchanged.

Batch 9 final acceptance: PR #56 merged at `16258ff62122ce7cbb7397c4994fc31013a59086`,
with a tree identical to final reviewed head `dd36128005907722b0fc51d648538dbba7af3ee8`.
All 13 PR checks pass; each platform passes 1,380 tests with zero failures/skips.
The exact-head minimum/current gh lanes each pass all 15 opt-in contracts. All 76
native proof files / 244 fixture rows were inspected, including 32 tracking cases
in eight native host proofs across four RIDs. Independent final review confirms
every original M01 criterion. Full results are retained in
`evidence/maintenance-batch-9-final.json`.
M01, M02, M04, M05 and M06 are complete; 16 original items remain.

## Batch 10: independent outcomes and unchanged updates

The M07 audit reproduced two remaining defects: equal updates replaced installed
native objects and consumed saved-plan slots, and later queue results erased
earlier stdout/stderr/notices. Regressions failed before the fixes. Independent
review also caught forward recovery bypassing the legacy equal-plan guard; its
regression failed before that path was corrected.

Fresh current checks now preserve the installed native objects, owner bytes and
timestamps without publishing a plan. Cleanup is limited to the never-published
private operation, through ownership duplicated from its held native handle.
Failure exposes the exact retained location. Apply and never-started ready-state
recovery of legacy equal plans avoid mutation; interrupted plans still recover.
Same-tree local repairs remain explicit reviewed replacements that retain the
edited original and report real file changes.

The Updates queue retains bounded output, diagnostics, notices, errors and earlier
mutation/failure evidence while keeping only the current plan as its Apply target.
Failed source requests retain both streams through staging wrappers and CLI/UI
rendering. Actual-gh tests prove first-success/second-failure behavior in both the
low-level adapter and the product saved-update queue.

Local build and style verification pass. All 1,426 default tests pass with zero
failures/skips, and all 17 opt-in contracts pass against gh 2.97.0 and 2.102.0.
Independent source, recovery, native ownership and proof reviews are clear after
addressing their findings. The raw-gh current fixture also exposed YAML key ordering and body-delimiter
presentation differences. A conservative same-revision comparison now preserves
those bytes while recognizing current content; new trees and meaningful changes
still require review. Its strict metadata snapshots are bound to held raw payload
and native object evidence. Both sequential AOT publishes pass. All 24 outcome
fixtures pass across both native hosts and both gh versions, including raw-gh
current checks, same-tree repairs and later remote failures. Exact-head remote
verification and merge remain required before M07 closure; local results and
hashes are retained in `evidence/maintenance-batch-10-update-outcomes-checkpoint.json`. See
`maintenance-update-outcomes.md` for semantics and proof commands.


Batch 10 final acceptance: PR #58 merged at
`9c4df912ffe632b4740bf7c4dd1e537f2beea9a1`, with the same tree as reviewed head
`9fe638699192e7fcc4e327b55e8f7ed64fee0077`. Copilot found an exact legacy
owner-transition compatibility regression; persisted tests reproduced it before
the fix, and actual plans saved by the prior native binary reproduced the same
inspection refusal. Validation now accepts only exact fresh or historical
serialization, preserving unapproved-field rejection. Both rebuilt native hosts
inspect, recover and undo old plans after the old Apply process is killed.

All 13 PR checks pass. Linux, macOS and Windows each pass 1,429 tests without
failures/skips; the exact-head minimum/current gh lanes each pass all 17 opt-in
contracts. All 84 native proof files / 292 fixture rows across four RIDs were
inspected, including 48 outcome rows. Final independent source, ownership,
evidence and original-criterion reviews are clear. Full final-source results,
hashes, legacy restart proofs and remote artifact attribution are retained in
`evidence/maintenance-batch-10-final.json`; the earlier checkpoint remains
historical evidence and is not attributed to the compatibility fix.
M01, M02, M04, M05, M06 and M07 are complete; 15 original items remain.


## Batch 11: managed and built-in skill discovery

M10 adds bounded Codex built-in and Codex/Claude plugin discovery while retaining
ordinary namespaces, ancestor workspaces, profiles and custom/shared roots.
Payload ownership, distributor versions and registration contexts remain separate
from authored source and unknown active-loading state. Doctor can include the
captured inventory; Installed details show managed provenance.

A shared live probe enforces read-only ownership in install/update/remove, cleanup
and maintenance state placement. It retains boundaries even when metadata is
malformed or omitted, limits external reads to explicit scopes, and preserves
captured agent environments through execution. Exact unrelated named imports
remain available; mutable --all sets beside managed children require immutable
staging before they can be supported.

Separate source/consumer/evidence reviews found alias, profile, late-adoption,
manifest and proof gaps; those corrections were re-reviewed clear. Final local
Inventory coverage passes 317 tests. Both sequential native publishes and all
36 managed-discovery rows pass across both hosts and gh 2.97.0/2.102.0. Both new
hosts inspect an actual old saved plan, recover the old Apply process after a
forced stop, and undo with original native objects/bytes/modes and exact registry
bytes restored, even after newly discoverable managed roots appear.

The final full suite passes all 1,467 tests with no failures/skips. Build/style,
Node automation tests, site build and actionlint pass. Exact-head remote checks,
all-RID native artifact inspection and merge remain required before closing M10. The acceptance ledger remains incomplete
until those results are recorded. Effective loading and plugin lifecycle remain
their separate original findings.


The first PR #59 source passed 1,467 tests on each platform, but native Linux
verification exposed unavailable-openat2 inventory failure and macOS verification
exposed incorrect inaccessible-root classification. Review identified the related
unknown-boundary cleanup path. These are fixed, permission regressions pass, and
all 319 Inventory tests plus both 17-case actual-gh contract suites pass. Source
and evidence re-review are clear. The follow-up checkpoint preserves attribution;
final full-suite, native and exact-head remote results remain required.


Copilot's PR #59 review added linked cache/synced entry-point, narrow unregistered
scope, reverse removal containment, proof-isolation and namespace classification
findings. Five new deterministic cases failed before corrections. Re-review also
covered nested synced links in metadata-only admission and direct-skill profiles
inside cached containers. Both independent reviewers are clear after those fixes;
all 326 Inventory tests pass. Known package observations survive contextual
rediscovery, captured managed roots are rechecked at removal execution, and
explicit physical payload reads do not expand to siblings. The original retargeted
alias test now independently proves captured state exclusion despite intentionally
partial linked-storage coverage. Final-source full/native/platform gates remain.


Batch 11 final acceptance: PR #59 merged at `d8405957c2d73167e60f50dad8ace8e6f000a58d`, with a tree
identical to reviewed head `5617ffdcaee408763ef8373d0d379cab5bd227a3`. All 13
checks pass. Local and each Linux/macOS/Windows suite pass 1,476 tests without
failures/skips. Both exact-head gh lanes pass 17 actual contracts (2.97.0 and
2.102.0). All 92 native proof files / 364 fixture rows across four RIDs were
inspected. Both local AOT hosts also pass 36 managed fixture rows, inherited-Pi
isolation and actual old-plan inspection, killed Apply recovery and undo with
original objects/bytes/modes and registry restored. Linux unavailable-openat2
proofs retain managed read access and destructive refusal.

Copilot findings and separate-model follow-ups are fixed and re-reviewed clear;
all six review threads are resolved. Final independent evidence and original-M10
acceptance review are clear. Full final-source hashes/results are retained in
`evidence/maintenance-batch-11-final.json`; earlier checkpoints remain historical.
M01, M02, M04, M05, M06, M07 and M10 are complete; 14 original findings remain.
The original Installed down-arrow crash was never reproduced; this closure does
not claim otherwise.


Batch 12 in progress: M11 identity and operation subjects follows merged PR #59.
The reviewed design and current implementation/review checkpoint are in
`maintenance-identities.md`. Separate source/consumer reviews were addressed and
re-reviewed clear. Inventory tests passed 344 cases at the earlier checkpoint;
the debug native App passed 16 disposable command rows across minimum/current
gh. Final-source full/platform/native/compatibility gates are pending. No original
finding is marked complete by this checkpoint.


Batch 12 final acceptance: PR #61 merged at
`351224b3e058166073370caafcba225bce12d7e9`, with a tree identical to reviewed head
`4fb69deb3fb1fae1a62bf6b68b0c37fc24d4b0ad`. All 13 PR checks pass. Local and each
Linux/macOS/Windows suite pass 1,515 tests with no failures/skips; both actual-gh
contract lanes pass 17 cases. All 100 native proof files / 468 fixture rows across
four RIDs were inspected and bound to GitHub artifact/head metadata. Both local AOT
hosts pass minimum/current gh identity, quarantine and update-outcome proofs plus
actual old-schema 1/schema 2 killed-Apply inspection, recovery and undo, restoring
original native objects/bytes/modes and exact registry. Copilot and independent
source/evidence acceptance reviews are clear. Evidence is in `evidence/maintenance-
batch-12-final.json`. M11 is complete; eight original findings are complete and 13
remain. The original Installed down-arrow crash was never reproduced. Next is full
original M12 effective agent loading, retaining all remaining M08 obligations.


Batch 13 final acceptance: PR #63 merged at `6233f6a39105c472130e726c425da7ecf5acb632`, with a tree
identical to reviewed head `bedba13fc1c1c16449122187aa2ade8f253c1c40`. All 13 PR checks pass. Local
and Linux/macOS/Windows suites pass 1,722 tests with zero failures/skips; focused
loading tests pass 204, and both minimum/current gh contract lanes pass 17. Both
local AOT hosts pass 32 loading commands plus four actual old-schema 1/schema 2
killed-Apply recovery/undo proofs. All four CI RIDs produced 108 verified proof
files / 532 commands and fixture rows, bound to fresh successful-attempt GitHub
artifact digests and native hashes. Source, proof and Copilot findings were fixed
and independently re-reviewed, including the final body-only Claude slash-state
finding. See `evidence/maintenance-batch-13-final.json` for exact attribution.
These are product fixtures and conditional cited local expectations: no actual
AI-client conformance or running-session observation is claimed. M12 is complete;
nine original findings are complete and twelve remain. The original Installed
Down-arrow crash was never reproduced. Next is full original M08, beginning with
external suppression and continuing preserve/merge/fork/source/owner transitions;
M03/M09/M13-M21 obligations remain unchanged.


Batch 14 merged: PR #64, merge `80921f49a5aefc07b01a1fc609a61132ddc4cd7c`, has the
identical tree to reviewed head `81072edbbac6d0a167aff5ae85e0bddfc6104c56`.
External cleanup suppression uses private bounded append-only state outside skill
payloads, stable source/owner namespaces, and native continuity plus bounded whole
payload evidence for unknown sources. Exact CLI selectors resolve agent aliases;
owned TUI operations drain before disposal and rescan after partial publication.
Legacy marker origins remain separate and external clear leaves markers intact.
Incomplete ownership evidence cannot activate/set suppression; state-home setup
failures are preference diagnostics and do not abort physical inventory.

All 13 PR checks pass. Local build/format, 1,752 full tests (zero failures/skips),
20 preference tests, both event-loop regressions and eight CLI cases pass. Both
sequential local AOT hosts pass minimum/current gh proofs: 116 commands covering
actual remote/local gh installs and ownerless copies with unchanged native payload
objects/modes and registry bytes. Four native CI RIDs produced 116 digest-verified
proof files: 232 new suppression commands plus 64 effective-loading commands and
468 prior fixtures. Both actual gh contract lanes pass 17/17. Independent store,
workflow, UI/CLI and proof reviews plus Copilot findings were corrected and
re-reviewed; both Copilot threads are resolved. Failed quota-limited and superseded
runs remain distinct from final successful-attempt evidence. Full attribution is
in `evidence/maintenance-batch-14-final.json`.

M08 remains incomplete. Non-installation/broken-link suppression, damaged/stale
preference recovery, preserve/merge/fork/source/owner transitions and the original
acceptance audit remain required. Nine of 21 original findings are complete.
The original Installed Down-arrow crash was never reproduced. Next: reviewed
whole-payload local dispositions, retaining all other original obligations.

## Batch 15: reviewed local dispositions and native metadata

Candidate `e3d8e47c08d66b9a5ba63b1a260ae9c206e967f0` implements preserve-installed,
immutable B/I/C review, explicit installed/source/captured-file choices, and schema3
merge plans. Approved source C remains the future baseline; customized result M
has separate result evidence. Apply/recovery/undo retain original native objects
and coordinate the existing supported gh owner transaction. Supported Unix and
Windows metadata is preserved through held objects; incomplete or unsupported
metadata remains an explicit publication refusal.

The October 16 Mac handoff reports 1,912 full tests passing at production source
`27432533cb09f57d682e699bcfd48764c98dc3a1`, native macOS App/extension shared-merge
and old-schema recovery proofs, three actual PTY flows, and Linux ARM64 real ENOSPC
recovery at all four move boundaries. The latter used a dedicated private 64 MiB
tmpfs and runtime apphosts, not NativeAOT. Those original temporary raw outputs
were lost after reboot. Preserved completion receipts and their hash manifest
are in `handoff-evidence-2026-10-16/`; they are historical receipts, not raw logs.
Earlier batch15 checkpoints retain their original interim scope and pending
statuses. This entry supersedes their pending verification status only where
the handoff receipts or independently retrieved GitHub evidence establish it.

On WSL, the published candidate and base `80921f49a5aefc07b01a1fc609a61132ddc4cd7c`
were verified, and full CI run
[38877432074](https://github.com/harder/gh-skillview/actions/runs/38877432074)
was independently confirmed successful at the exact candidate. Dependency review
was skipped on that branch dispatch; PR-only checks remain required. Production
source and ordinary test trees are identical between `2743253` and `e3d8e47`.
The transferred supporting documents match the candidate's tracked copies.

The focused independent Sol integration review is clear. All four retrieved
native artifact ZIPs match their GitHub SHA256 digests, and every extracted member
matches its ZIP member. The 156 retained files include 30 new local-merge proof
files whose required assertions were checked: App/extension publication and six
shared recovery scenarios per host on macOS/Windows; four move-boundary restart
proofs on each of those platforms; Linux unprivileged metadata refusals and four
real-ENOSPC recovery proofs. Each ENOSPC witness reports errno28 and zero available
bytes in its 64 MiB tmpfs. Both Windows native metadata logs prove 98/98 tests.
The CI native fixtures use each runner's gh (including 2.100.0 on macOS), separate
from the minimum/current contract lanes and historical Mac proofs. Exact artifact
and per-file hashes, checked assertions, command outcomes and review scope are in
`evidence/maintenance-batch-15-verification.json`.

The WSL ARM64 scoped test-project locked restore succeeds with SDK10.0.402.
All 66 focused local-review, three-way-review, Linux ABI and Updates-tab tests
pass with a private `/var/tmp` TMPDIR. The initial run's two state-placement
refusals came from the sandbox's read-only `/tmp/.git` mount; the failed run is
retained separately. Full solution locked restore infers an uncommitted host RID
on this machine, so no local release-publish claim is made and release locks stay
unchanged. PR65 Copilot review found stale Windows publication status in the user
guide; the correction describes supported metadata, effective-user ownership and
the existing security-privilege requirement while retaining explicit refusals.

### Batch 15 final merged verification

PR65 merged at `0dba59be59776e6e654123aa1ce1e5496074763e` on 2026-10-16.
The merge tree `9335a2e961cf488069a4a79b392599bda33f09a0` matches the exact
reviewed head `81e403db602ccc4c9f82c629810f8b6d01dde3ac`; the merge parents are
the prior main commit and that reviewed head. Final evidence is in
`evidence/maintenance-batch-15-final.json`, including the selected artifact
digests and member hashes, attempt attribution, observed PR checks, tests,
metadata results and the quota-limited first attempt. All 15 observed PR check
entries are successful after attempt 2; CodeQL passed in its separate run. The
successful Linux/macOS jobs, platform tests and Windows metadata jobs are retained
from attempt 1; the Windows AOT results are fresh attempt-2 replacements. The three
platform test jobs each passed
1,912 tests; both Windows metadata jobs passed 98 tests. The final artifact set
contains 156 verified members across four native RIDs, with 30 new proof
contracts checked. Attempt 1's Windows failures were GitHub Actions installation
API quota responses; only the failed Windows jobs were rerun after reset on the
unchanged reviewed head.

Batch 15 adds preserve-installed and reviewed schema 3 merges. Approved source C
remains the future comparison baseline, while customized result M has separate
result evidence. Supported native metadata publication is verified across Linux
x64, macOS ARM64, Windows x64 and Windows ARM64. Original acceptance remains
9/21; M08 fork/source/local-import/standalone unpin, required detach, other-owner
transitions, remaining preference recovery and the original criterion audit
remain open. The original Installed Down-arrow crash was never reproduced.

## Batch 16: standalone unpin (local checkpoint)

The pre-candidate working tree adds schema 4 `unpin-only`, which binds captured
installed content and pin policy without fabricated revision/base evidence. It
removes verified raw pin members while retaining source/ref/tree and physical
exposures, and uses checked effective-owner state across save, recovery and undo.
Local final-targeted verification passes the scoped build, 18 standalone-unpin
tests, and Core/test formatting. The earlier 1,959-test full-suite result belongs
to the source snapshot before the final reviewed source changes; it is recorded as
pre-final evidence, not a final-source full-suite claim. Native AOT on four RIDs
and compatibility with gh 2.97.0/2.102.0 remain pending CI. M08 and original
acceptance remain incomplete at 9/21; other original obligations are unchanged.
The original Mac Down-arrow crash was never reproduced. See
`maintenance-standalone-unpin.md` and
`evidence/maintenance-batch-16-local-checkpoint.json` for the design and bounded
local evidence.

### Batch 16: failed CI and local cross-platform fixes

PR66 attempt 1 at `b8ab8fa475960308c71e556ee5ac94a3983b65c4` was canceled after
macOS and Windows unpin tests/metadata each exposed 16 failures: strict schema4
archive validation compared the wrong platform projection. The macOS AOT lane also
selected tar.gz for the official gh 2.97.0 arm64 asset, which is a ZIP; Copilot
reported the same helper defect in one unresolved review thread. The schema4-only
native comparison and macOS ZIP fixes were independently reviewed. On the fixed
local source snapshot, the scoped build, StandaloneUnpinTests 29/29, shared-owner
regressions 69/69, Core/test format checks, and three downloader-helper tests pass.
Official macOS gh 2.97.0 and 2.102.0 ZIPs were checksum verified but not executed.
The earlier 1,959-test full-suite result remains attributed to its pre-final source
snapshot. Fresh cross-platform CI and four native AOT lanes remain pending.
Frozen schema 1–3 compatibility uses records from baseline `0dba59be` with gh
2.102.0 on both proof hosts; the separate unpin compatibility matrix uses gh
2.97.0 and 2.102.0. Attempt1 produced no selected native proof artifacts.
Acceptance remains 9/21 and M08 incomplete. Details, raw log hashes,
artifact digests and exact local source manifest are in
`evidence/maintenance-batch-16-ci-fixes.json`.

### Batch 16: frozen legacy fixture follow-up

At exact head `5ad2c4eb65b552fa5aaf9c44e66036c59589ec13`, all three full platform
suites pass 1,987/1,987 and both Windows metadata lanes pass 207/207; CodeQL and
both gh contract lanes also pass. The Linux native lane reached four standalone-
unpin `CAP_SYS_ADMIN` refusal rows (App/extension × gh 2.97.0/2.102.0), then
failed its schema1 restore-plan fixture because the fixture injected noncanonical
lockfile fields before the historical guard. The workflow was canceled for that
fixture issue; the four native artifacts are partial and do not establish a full
RID matrix, privileged Linux success, or schema 1–3 compatibility. The helper fix
keeps schema1 canonical and moves extension injection to later schemas; three
focused helper tests, AST parsing and help checks pass locally, but fresh native CI
remains pending. The earlier 1,959-test full-suite count remains pre-final-source evidence.
Original acceptance remains 9/21 and M08 incomplete. See
`evidence/maintenance-batch-16-legacy-fixture-fix.json` for run IDs, artifact
SHA256 digests, partial proof scope and the local helper receipt.

### Batch 16: native proof harness path follow-up

At `4130a3f4`, CI attempt1 exhausted the installation API quota in existing
proofs; the observed response reached 5,000 used, zero remaining, with reset at
19:21:28 UTC. The failed-job rerun after reset completed the full Linux native
lane, including private privileged unpin and frozen old-schema proofs; artifact
assertion review is clear: 60 artifact members, 36 privileged unpin recovery rows
and six actual old-schema fixtures were verified. macOS failed an existing shared-owner
update preparation on a refused outbound connection. Both Windows lanes failed
when the shared-owner App proof tried to hash an extensionless `gh` pathname;
Windows had launched `gh.exe`, but the receipt read required its actual filename.
These failures precede the new unpin/legacy steps on those three platforms.

The helper now binds every explicit executable before authentication or fixtures,
preserving downloaded versions and refusing missing paths without PATH fallback.
Four focused helper tests, AST/help and diff checks pass; independent review is
clear. Product source/tests are unchanged from the recorded 3×1,987 platform and
2×207 metadata passes; corresponding `4130` gates, CodeQL and both gh contracts
pass. New-head CI and a complete four-RID native proof selection remain pending.
Acceptance remains 9/21 and M08 incomplete. See
`evidence/maintenance-batch-16-harness-path-fix.json` for exact attribution and
compact raw-evidence hashes.
