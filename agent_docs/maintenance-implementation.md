# Skill maintenance implementation and evidence

The 2026-10-13 audit contains M01–M21. Implementation proceeds through separate
PRs. Each batch needs local tests, disposable runtime proof, independent review,
resolution of accepted findings, re-review, required CI, and merge before the next.
The source audit is `skillview-maintenance-research/backlog.json` attached to the
originating Codex chat. No installed user skills are test targets.

## Batches

1. Inventory facts and removal classification: M01, M02, M04; protective portion
   of M03 and foundations for M11/M12. Implemented; independent review clean; PR/CI pending.
2. Exact updates and complete results: M05–M08, including local-change protection.
3. Sources, lock ownership, bounded payload comparison and discovery: remainder
   of M03, M09–M11.
4. Recoverable repair plans, relinking and consolidation: M13–M15.
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
