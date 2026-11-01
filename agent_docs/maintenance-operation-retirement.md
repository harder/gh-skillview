# Explicit retained-operation retirement

History listing stays existing-only, bounded, and non-authoritative. The TUI
runs retirement inspection through `ReplacementPlanStore.InspectHistoryAsync`;
CLI `history` and `replacement-history` keep their existing semantics/wire shape.
No CLI mutation command or shipping fault switches were added.

`Abandon unapplied review` requires only the immutable candidate/base/archive
slots, no operation steps and an absent owner journal or one with no intents.
The live location may have changed or be missing. `Discard Undo data` requires
an exact retained original (or the terminal restored displaced slot), a locally
terminal owner journal and a CURRENT live directory, of any content/source.
Missing live data protects Undo evidence: it may be the only copy. Neither action
can delete a live copy, change registry bytes, retarget consumers or evict peers.

Retirement uses its own narrow private-evidence inspector. It does not call
ordinary Observe/Admit, require historical context fingerprints, or open the live
registry through ordinary owner admission. Registry state, roots and consumer
memberships may have advanced. Ordinary Inspect/Apply/Recover/Undo and schemas
1–6 keep every existing admission/context/hash rule unchanged. Superseded live
copies disclose: “The live copy changed since this operation; Undo was already
unavailable.” History can offer retirement even when specialist inspection refuses.

Capture placement inventory, acquire existing root writer then operation writer,
and capture fresh uncached complete inventory under those locks and at slot
boundaries. Keep placement exclusions, EnsureNoConsumers on every private slot,
and checks of historical membership LOCATORS at their current destinations, never
as equality requirements. Reject private ownership/consumer overlaps, partial
coverage, active IDs/pending publication, unfinished local journals, undo-recovery,
historical merges, unknown names/types, aliases, changed identities and contention.
Compare all private roots, descendants AND small files against a freshly captured
CURRENT live root/descendants; never compare aliases against historical candidate
IDs. Internal private aliases also remain protected. UI IDs are copied on the UI
thread and frozen by the store before background awaits.

The strict bounded `retiring.json` sidecar now uses version 2, adding exact current
live native/byte evidence (including children), or explicit missing-live evidence
for abandonment. Existing version-1 markers retain their exact resume checks,
deriving their unchanged historical live children from the immutable plan. No
marker migration/replacement write is added. Before/CurrentLive consistency is
validated. The 16 MiB bound, duplicate/unmapped-member refusals, immutable plan
hash, local terminal topology, private state/operation/child identities and exact
small-file bytes remain required. Old readers still protect the unknown filename.
Malformed/partially written markers remain protected; no replacement is offered.

For a real owner transition (different before/desired fingerprints), installed or
restored payload topology and an empty journal do not prove publication. Require
the last completed payload move followed by the matching owner intent/completion
steps and a validated finished raw journal write. This local proof permits later
registry advancement or reversion. Without it, use a bounded held native registry
read at the saved exact address/selector in the captured HOME context; refuse
installed+before, restored+desired, torn, ambiguous, unsupported or unreadable
state. Readable satisfied, foreign/advanced or absent records may retire. No
registry writes or clients are used. Equal transitions need no publication proof.
Recheck admission at inspection, approval, every slot boundary and marker resume;
fallback observations bind the approval fingerprint. A marker alone proves no
owner completion. Within one held invocation, actual verified local completion
can survive deletion of its own journal only after all payload slots are gone;
a restart must prove completion anew or use the bounded registry observation.
An unfinished transition remains available to ordinary Inspect/Recover before any
retirement marker is created. The only ordinary execution code change is an inert
existing-checkpoint-seam probe immediately before the owner publish precheck, for
reproducing the exact race in disposable proofs; production authority is unchanged.

Abandonment of a local-reimport review with inspected missing live data and a
verified installed-capture explicitly warns that this capture is also discarded
and may be the only remaining copy. This wording uses existing inspection evidence
and changes neither admission nor I/O. Cancel remains the default.

Create the marker under the writers before any deletion, capture the operation's
post-create generation through its held lease, write and flush the marker and
publication, then re-read its exact bytes. Malformed/partial publication remains
protected. Remove candidate/original/displaced/recorded-base slots separately
first, then bound known archive/terminal-owner evidence directories. Keep the
plan/marker siblings through all directory removals. Each removal retains the
exact owned native lease and revalidates the entire surviving set. Native callbacks
reject unrecognized/replaced children and recheck file bytes/IDs/generations.
Cancellation immediately precedes native destructive calls, including retries
inside the existing backend. Every removal requires root-removed and zero errors,
followed by publication flush. No recursive managed/path deletion or unsupported
platform fallback is permitted.

Explicit resume requires a new inspection and fingerprint-bound action approval.
Only whole approved directories may be absent. All remaining trees must match
full captured native IDs/generations and bytes; all small files must remain exact.
Revalidate surviving archive manifests/content independently against the immutable
plan, plus the exact live observation bound when retirement started, state root,
current consumers and locally terminal owner journal. Hold the observed live root
through removal; recheck its native identity/address and parent immediately before
each destructive native call. Full live evidence is reverified after checkpoints
and between slots. Disappearance/replacement or a new live observation protects
all remaining evidence; a new inspection does not rebind a started marker.
Stable parent IDs or marker JSON alone never authorize changed descendants.
When no approved directory remains, verify known small files, close Windows
operation/lock handles, remove only that held operation and close the owned lease
before flushing the parent under its still-held root writer.

Existing primitives cannot safely infer progress inside a partially removed tree
or final small-file cleanup. Those remnants stay protected with a concrete reason;
no name-only resume or manual deletion recipe is provided. A parent publication
failure after root removal is reported as unverified, never success; the directory
may already be gone. Quota counts actual remaining operation directories and is
never raised/bypassed. Per-tree capture/marker bounds can also refuse oversized
retained evidence rather than relaxing them.

History owns background requests through application helpers and awaited guarded
UI dispatch. Selection/refresh/Inspect/closing consume old approvals. Confirmations
show exact ID/fingerprint/consequences, with Cancel default at 80/120 columns.
Mutations disable competing controls; modal cancellation does not abandon app
owned work or final history/badge reconciliation. Native update Undo is preserved.

`tests/native/prove-retained-retirement.py` runs offline positive/kill/restart
proofs through the proof-only host and product listing. CI invokes it for both
product hosts in every RID's existing inspection-unpin shard. Unit coverage
includes 16→17 admission, stale approval, native substitution, locks, cancellation,
partial remnants and shared owner/consumer preservation. Cross-OS execution and
historical native compatibility are separate evidence; do not claim them from a
Linux managed run.

## Registry-recorded damaged-original plans

`restore-registry-recorded` retirement keeps the immutable observed damaged
metadata separate from desired tracking. The first retirement open retains the
paired raw archive checks. Marker resume independently revalidates the installed
observed-policy half and candidate desired-policy half for every surviving
manifest-bound archive; only H2's already-approved whole-slot absence may resume.
Partial archives and altered blobs or metadata remain protected.

Retirement opens retain the saved catalog memberships' sorted, unique, known-agent,
same-address, nonlink, user-scope structure, while omitting only fresh catalog
recomputation equality. Ordinary Inspect/Apply/Undo/Recover still require that
captured-context equality. Current inventory, placement, native, consumer and
owner guards remain mandatory for retirement.

An empty retained directory is allowed only for this purpose's exact immutable
empty original under installed starting topology. Both saved and current entry
and child lists must be empty, payload/mode must agree, and stable native object
identity must match. A native retain rename changes the root generation, so never
compare the pre-rename `Original.Objects` hash to the retained root's current
hash or rewrite either observation. Existing marker and held-directory checks
still compare the full current identity/generation, Objects, payload, mode and
children before every removal and on resume. Every other empty slot stays refused.

`tests/native/prove-registry-recorded-retirement.py` exercises this interoperability
with genuine installed registry records, both native product hosts and the
existing proof host's checkpoint mechanism. Its explicitly supplied offline mode
is only a frozen source transport supplement, never actual gh live mutation.
Preserve failed fixtures separately from fresh destructive proof fixtures.

The retirement proof host must preserve its original scanner options (null options,
`AllowHiddenDirs=false`) while using cancellation-aware scanning and carrying
inaccessible paths and discovery locations into `ScanDiagnostics`. A link into a
missing-SKILL retained directory is an unsupported linked container, hence partial
coverage, even when no skill row is emitted. The proof-only `inventory` action
exposes this observation for comparison with actual product listing. A declared
complete snapshot built by dropping scanner diagnostics cannot prove admission.
