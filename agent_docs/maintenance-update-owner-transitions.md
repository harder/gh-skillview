# Batch 6 remaining integration: shared owner transitions

This records the independently reviewed integration design after the ownerless
saved-update and native registry-session checkpoint. It is required work within
the current batch, not a reduction of the original 21-item objective.

## Saved contracts and authority

- Extend schema 2's optional revision with a verified single gh owner transition:
  canonical registry address, selector, raw before/desired selected entries and
  fingerprints. Preserve source, source subpath, installed timestamp and unknown
  extensions; only tree, unpin policy and updated timestamp may change.
- Retain schema 1 restoration fingerprints and the existing native payload moves.
- Open a registry session before the first observation in apply/recovery and
  retain it until payload moves, owner publication and terminal journaling end.
  Perform no network work while holding that execution lease.
- Planning may briefly open/release a session before and after remote staging,
  revalidating the same selected entry and native authority before publication.
- Keep stable roots/context/consumer evidence separate from the mutable owner
  state. Add an optional owner observation with before/desired/torn/foreign state,
  whole-file hash/length and full native identity. Include it in recovery review
  fingerprints and omit it for schema 1.

## Same-handle reads and bounded evidence

Pass an optional held-owner context through Observe, Admit, Context and ReadOwners.
The contextual lock reader must use the held raw bytes for every alias of the
canonical leased registry. Keep all other ownership/consumer checks. Windows
mandatory byte locks reject second-handle reads even within the same process.
The narrow GhSharedLockGuard writer-schema check belongs to gh's typed writer;
use the preserving editor's schema/entry check for this transaction so extensions
are retained. Source/subpath/selector matching remains mandatory.

Use a bounded owner-journal subdirectory with a retained writer.lock handle.
Bind every intent/completion to the saved plan fingerprint, direction and write
ID. Retain incomplete evidence, bound total attempts/bytes and reserve enough
capacity for the entire remaining transition and rollback/recovery. Registry
mutation counts remain explicit even if that invocation makes no payload move.

Entry-level validation can rebase unrelated valid changes during initial apply.
Explicit recovery additionally supplies its reviewed whole-file snapshot to
WriteSelection so any intervening byte/native change requires new inspection.

## Accepted phases

| Payload state | Selected owner | Forward recovery | Undo |
| --- | --- | --- | --- |
| Ready | Before | Retain, install, publish desired | No-op |
| Original retained | Before | Install, publish desired | Restore original |
| Candidate installed | Before | Publish desired | Restore original |
| Candidate installed | Desired | Complete | Displace, restore, revert selected owner |
| Replacement retained | Before or desired | Require undo direction | Restore, then revert owner if needed |
| Original restored | Desired | Refuse plan reuse | Revert selected owner |
| Original restored | Before | Refuse plan reuse | Complete |
| Any recognized state | Foreign | Refuse | Refuse |
| Any recognized state | Torn | Explicit reviewed repair only | Explicit reviewed repair only |

Forward writes patch the desired entry into fresh locked bytes after candidate
installation. Undo patches the original entry into fresh locked bytes after
original restoration. Unrelated manager changes survive both operations. A live
write failure with PreimageRestored means known before-owner state, not successful
completion. Reobserve and journal that phase without pretending the payload was
rolled back too.

## Torn-file inspection and reviewed repair

The existing session constructor requires a valid selected entry. Add a distinct
raw recovery-open mode that exposes bounded raw snapshots without admitting
ordinary WriteSelection. Keep consumer coverage and all unrelated owner failures
mandatory; bypass only diagnostics attributable to this exact saved registry.

Inspection exposes current raw hash/native identity and retained applicable
write evidence. Repair must bind to the exact reviewed current snapshot and a
validated before/desired image from this plan's applicable write intent. Publish
fresh plan-bound repair intent, recheck through the held lease, write/flush and
verify. Foreign bytes remain a refusal. Never automatically replay a stale whole
registry after process death. If JSON is valid, patch only the selected entry
into current bytes so unrelated changes survive.

## Required proof before merge

Exercise custom/ownerless and shared gh installs, multiple exposures, pins/unpin,
local edits/extras, nested namespaces and identical names with unrelated owners.
Test live gh contention, current unrelated edits through apply/undo, stale review,
foreign selected records, malformed/extended schemas, every payload/owner write
interruption, actual process death/restart, torn-byte review, journal exhaustion,
owner write refusal and terminal accounting. Route ordinary CLI/TUI updates
through reviewed immutable stages, then run full platform tests, four-RID both-host
AOT proofs and independent review/re-review before merging batch 6.
