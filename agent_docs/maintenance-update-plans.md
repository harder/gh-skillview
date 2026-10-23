# Recoverable updates and gh owner transitions

Saved updates freeze one immutable candidate for an exact installation. Both
ownerless copies and verified shared gh installations use explicit review,
retention, apply and recovery. See [saved updates](../docs/saved-updates.md).

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

## Components and compatibility

`GhRegistryEditor` prepares bounded selected-entry patches without I/O or write
authority. `GhRegistryLease`/`GhRegistryWriteSession` retain the existing file
object and gh-compatible lock through payload and owner publication. Same-handle
reads are mandatory on Windows. Journal raw before/desired evidence before
truncation; retain foreign bytes for explicit repair instead of restoring over
unrelated changes. Explicitly unlock Unix descriptions before closing.

Schema 2 preserves recorded base, local changes and the desired source/pin
transition. Schema 1 hashes remain compatible. Current recovery, source review,
unpin and metadata rules are in [saved-operation contracts](contracts-saved-operations.md)
and [owner transitions](maintenance-update-owner-transitions.md).

## Ordinary entrypoints

Product updates separate prepare, review and apply of the saved
plan. Keep `GhSkillUpdateService` as a low-level gh contract adapter; the product
must not fall back to mutable `gh update` after reviewing an immutable stage.
CLI `update` prepares exact installations and reports the existing explicit
`update-apply --id --expect --yes --disposition retain-original` command. A force
request is not approval to discard local content.

The Updates tab's `u`, `U` and Update actions start an embedded review queue.
Capture physical target identities and policy before awaiting, show the saved
commit/tree/ref, affected paths/consumers, local changes, pin transition and root
modes, then expose a separate Apply reviewed plan action. Keep workflow ownership
in `SkillViewWorkflowCoordinator`, native I/O off the UI thread, and preserve
durable outcomes plus inventory invalidation even when navigation cancels work.

Prepare/review/apply one queued installation at a time. Applying a sibling
changes the shared root/parent generation and invalidates other plans prepared
beforehand; silently Inspect/Recover-ing them would bypass their approval.
A future single-approval multi-target transaction needs explicit batch authority
accounting for its own prior mutations and revalidating untouched targets.
Pins remain skips unless explicitly unpinned. Report independent successes,
refusals, recovery-required results and not-started targets. Availability checks
must not consume the 16 retained-plan quota; explain quota failures when staging.
Required integration proofs include all shortcuts, sibling queues, stale
selection/navigation, no second source resolution on apply, cancellation and
partial failures.
