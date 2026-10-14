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
