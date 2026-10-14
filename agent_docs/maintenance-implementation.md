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
   Local changes refuse replacement; explicit dispositions, durable backups and
   recovery complete M08 in batch 4.
3. Sources, lock ownership, bounded payload comparison and discovery: remainder
   of M03, M09–M11.
4. Recoverable repair plans, relinking and consolidation: M13–M15 and remaining M08.
5. Native owners and agent visibility: M12 and M16.
6. Desired state, adoption, skill diagnostics and maintenance UI/CLI: M17–M20.
7. M21 contract/adversarial verification accompanies every batch, followed by a
   final acceptance audit against every original criterion.

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
