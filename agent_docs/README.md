# Agent documentation

Use [AGENTS.md](../AGENTS.md) for repository-wide invariants and commands, and
[deferred-maintenance-work.md](deferred-maintenance-work.md) for scope decisions.
The former all-21 maintenance goal is retired. Dated implementation inventories
and verification results describe their observed revisions, not a current backlog
or release verdict.

## Current guides

- [Running tests](running-tests.md) and [terminal testing](tui-pty-testing.md).
- [Saved updates](../docs/saved-updates.md), [replacement plans](maintenance-update-plans.md),
  [local dispositions](maintenance-local-dispositions.md), and
  [standalone unpin](maintenance-standalone-unpin.md).
- [Managed discovery](maintenance-managed-discovery.md).
- [Explicit retained-operation retirement](maintenance-operation-retirement.md).
- [Static package checks](maintenance-static-package-check.md),
  [source review](maintenance-update-source-review.md), and
  [maintenance queue](maintenance-queue.md).
- [Version selection scope](version-selection-scope.md).
- [GitHub CLI 2.103.0 compatibility](gh-2.103.0-compatibility.md)
  (authentication/extension baseline) and
  [2.103.1 compatibility](gh-2.103.1-compatibility.md) (patch review).

Detailed subsystem invariants are indexed by the short root [AGENTS.md](../AGENTS.md).
Active native proof tools and their tests live in [tests/native](../tests/native/README.md).
Historical compatibility and UX results remain in [evidence/](evidence/).
Saved operation plans, backup manifests, journals and recovery state are product
data; they are not disposable development plans.

## Retired implementation records

The October 23 cleanup removed the old batch ledger, audit/acceptance snapshots,
handoff receipts and `maintenance-*.json` checkpoints from the current tree.
Their original bytes remain available at the immutable pre-cleanup revision:

- [Implementation ledger](https://github.com/harder/gh-skillview/blob/0f7264f7eca48f6329f9a82d60fbf803e43dd5bd/agent_docs/maintenance-implementation.md).
- [Original audit](https://github.com/harder/gh-skillview/blob/0f7264f7eca48f6329f9a82d60fbf803e43dd5bd/agent_docs/maintenance-audit.json) and
  [acceptance snapshot](https://github.com/harder/gh-skillview/blob/0f7264f7eca48f6329f9a82d60fbf803e43dd5bd/agent_docs/maintenance-acceptance.json).
- [Batch and checkpoint evidence](https://github.com/harder/gh-skillview/tree/0f7264f7eca48f6329f9a82d60fbf803e43dd5bd/agent_docs/evidence).
- [October 16 handoff receipts](https://github.com/harder/gh-skillview/tree/0f7264f7eca48f6329f9a82d60fbf803e43dd5bd/agent_docs/handoff-evidence-2026-10-16).

Use these records when investigating historical changes. Follow current guides
for implementation, testing and prioritization. Retired local work was archived
before obsolete checkouts and branch refs were removed. Private archives retain
unmerged commits, dirty source snapshots and merge/index evidence; they are not
published in this repository. Current refs and worktrees do not reproduce the
historical inventory. Unique failure/proof receipts remain available locally.

- [Inventory and tab performance](inventory-performance.md): measured flow, complexity, cache ownership, freshness boundaries, and reproduction.

- [Registry-recorded restore](maintenance-registry-recorded-restore.md): bounded missing/corrupt original retention, independent source selection and exact Undo.
