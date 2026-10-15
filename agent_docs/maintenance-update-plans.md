# Batch 6: recoverable updates and gh owner transitions

Status: in progress on codex/skill-maintenance-update-plans, based on merged
PR #52 (178476f7d8b0c9f7ab6cb5dcf4066410f0570c7c). Scope: a complete
single-installation update workflow for ownerless custom copies and
shared gh installations. The original 21-item acceptance ledger remains active.

## Required workflow

1. Resolve gh's latest-release/default-branch policy to an immutable commit and
   the existing source subpath's Git tree. Preserve routine pins; unpin requires
   explicit review. Source changes and renamed skills need separate migration.
2. Reuse the held native stage and saved manifests. Capture recorded-base drift,
   original/candidate changes, exact paths, consumers and desired tracking.
   Retain the original as the explicit local-change disposition.
3. For a shared install, acquire the existing registry DATA FILE's gh-compatible
   lock before the first payload move. Revalidate the selected owner entry and
   native parent/name/file identities. Keep that handle throughout the operation.
4. Prepare the desired registry from the current locked bytes. Change only the
   selected entry; retain unrelated entries, extensions and original installedAt.
   Publish bounded durable raw before/desired bytes and write intent first.
5. Retain original, promote exact stage, then write/flush/verify through the held
   registry handle and publish completion. Never resolve a moving ref again or
   invoke gh update against the live target after plan approval.
6. Inspect/recover/undo bind both payload slots and current whole-file bytes/native
   identity. Valid unrelated manager changes survive selected-entry rollback.
   Unexpected selected entry changes refuse before displacement. Torn bytes
   require explicit repair review with retained before/desired evidence.

## Upstream authority contract

gh 2.102.0 locks .agents/.skill-lock.json itself: Unix flock exclusive/nonblocking,
Windows LockFileEx exclusive byte zero, length one. It seek/truncate/writes that
same file. A sidecar does not coordinate, and replacing the registry pathname
can strand a cooperating writer on an old inode. The new lease must use matching
locks and preserve existing permissions.

Primary sources:

- https://github.com/cli/cli/blob/v2.102.0/internal/skills/lockfile/lockfile.go
- https://github.com/cli/cli/blob/v2.102.0/internal/flock/flock_unix.go
- https://github.com/cli/cli/blob/v2.102.0/internal/flock/flock_windows.go
- https://github.com/cli/cli/blob/v2.102.0/internal/skills/discovery/discovery.go
- https://cli.github.com/manual/gh_skill_update

An in-place write can tear on process death. Another manager can act after the
lock is released by death, and gh treats corrupt content as a fresh registry.
Private evidence supports reviewed recovery; it cannot establish that an
external writer preserved all data. While alive, attempt bounded preimage
restoration under the same lock after write failure. Never blindly restore an
old whole-file snapshot over unrelated post-crash updates.

## Implementation and verification

GhRegistryEditor is the first component. It prepares bounded bytes without I/O,
preserves raw bytes outside the selected entry, and rejects ambiguous schemas,
duplicate keys and selected-entry drift. It supplies no write authority. It has
independent source review and focused preservation/refusal tests.
Current component verification: clean build and 12 focused tests passed.

Still required before this batch is ready:

- Native existing-file registry lease with matching gh locks and held authority.
- New-revision resolution, saved before/desired owner evidence and phase-aware
  observations in the replacement engine.
- CLI and ordinary product-update routing through the saved stage.
- Custom/shared/pinned/local-edit/namespaced real-gh proofs, concurrent gh lock
  contention, every payload/registry interruption boundary, torn-write review,
  unrelated record preservation through apply/undo and unwritable-owner refusal.
- Full tests, both native hosts, four-RID CI, independent review/re-review and merge.
