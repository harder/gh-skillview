# Target-backed exposure link retention (primitive checkpoint)

This slice supplies an internal native primitive, not a detach command, saved
plan, manager adoption or claim about an agent's effective loading. Original
M08 remains incomplete; acceptance stays at 9/21 original findings.

`NativeExposureLinkLease.Open(root, entry, targetLease, token)` binds a single
ordinary directory symlink to an independently held skill-directory lease. Its
root/parent namespace, full link ID/generation, exact raw target, full target
ID/generation/address and bounded metadata observation are separate evidence.
The link object is pinned without following it for the lease lifetime, preventing
inode/file-ID reuse while retention is attempted. Caller-owned target and state
leases must outlive this primitive.
For the default low-level API, the caller must hold its owning operation's
writer lock and complete the owning writer/device publication barrier after the
primitive flushes link-slot namespaces. The Linux facade owns that writer and
barrier internally. Namespace flushing cannot substitute for the operation barrier.

`NativeRetainedLinkSlot.Create(operation, name, token)` creates a private typed
child with one fixed possible leaf, `entry`. Initialize every private namespace
before freezing source/target generations. The slot borrows the existing held
state authority through a platform-specific bridge; generic state-file and
directory-relocation policy is unchanged. Generic state enumeration continues
reporting a retained link as an unsupported entry. State must be outside known
installed/managed roots; `Retain` additionally refuses storage beneath its
exposure or target scan root. All other placement/ownership/consumer admission
belongs to a future saved operation, not this primitive.

`Retain(slot, token)` exclusively renames the native link into its empty slot.
`Restore(token)` renames that same object back only while the original name is
vacant. Source and destination must share a native filesystem/volume. Each move
checks full identities, generations, raw target and supported link metadata;
bounded immediate namespace observations must differ by exactly the moved name.
Unrelated siblings, foreign occupants and moved ancestors refuse. A target
whose frozen root/parent equals either changed parent also refuses: the existing
target lease cannot safely advance that generation, so this slice does not
refresh it. Coordinated shared-parent support remains a later explicit contract.

Relative retained links are intentionally not followed at their private
address. Restoration resolves captured raw bytes against the ORIGINAL held
parent and compares the resulting directory's full native evidence with the
still-held target. Windows accepts only the existing supported symbolic-link
tag and bounded valid relative/local-drive substitute paths; selected junctions,
mount points, UNC/device targets and unknown reparse tags refuse. Invalid Unix target
encoding, broken/non-directory targets and volume changes refuse.
The raw target's final component must itself be an ordinary directory; a link
to another link is refused even if both currently resolve to the held payload.

Windows raw targets containing `.` or `..` use bounded
[`Path.GetFullPath(path, basePath)` normalization](https://learn.microsoft.com/en-us/dotnet/standard/io/file-path-formats#path-normalization)
against the original held parent's fresh native address. This implements
[Windows link-substitution dot semantics](https://learn.microsoft.com/en-us/windows/win32/fileio/creating-symbolic-links)
for a read-only candidate open. Its actual opened canonical address and full
native identity/generation must match the separately held target, with full
original-parent identity/address checks before and after. The path is never
rename/delete authority. No-dot NT opens retain their existing intermediate
reparse behavior; final reparse, volume and metadata guards remain unchanged.
Actual Windows tests compare native followed links and raw witnesses across
leading/nonleading dots, erased missing components and junctions, and retention
while the original entry is absent. Local Linux runs cannot establish this
Windows parity.

Only the link is renamed; the target payload is never written. Original and
post-rename identities remain distinct: creation identity/native ID must remain
the same, while change-time/generation can advance. The raw target and applicable observed metadata fingerprint remain equal. No unlink/recreate, overwrite, copy/delete, archive
restoration or cross-volume fallback exists.

The default `FullMetadataVerified` mode keeps full supported metadata as its
mutation gate. macOS/Windows reuse existing bounded capture, including supported
attributes or full Windows security/backup streams; unavailable privileges or
unsupported observations refuse. Linux still reports unobservable symlink inode
flags and any missing trusted-xattr visibility. Even CAP_SYS_ADMIN does not
remove those full-metadata refusals.

Linux has a separate explicit `LinuxSameOriginalObjectPreservation` mode, admitted
only through `LinuxExposureOwnerContext`. The ordinary borrowed-target overload
refuses that mode before creating native authorities.
It retains and restores the continuously held original native link on a verified
ext4 mount. It does not clone metadata or set `Metadata.Supported` to true.
`Preservation` separately records mode, effective-user ownership, group, exactly
one native link, all caller-visible attribute names/values and their fingerprint,
and the held filesystem/mount/task/namespace evidence. A visible attribute whose
value cannot be read, unstable names/values, incomplete/bounded proc evidence,
unknown filesystem, or any authority mismatch refuses. Hidden namespaces and
inode flags remain unknown; keeping the inode is not exhaustive metadata evidence.
The opaque original can preserve hidden state without proving equality against
concurrent changes. Rename changes ctime/parent generations; old timestamps and
path-based security decisions are not preserved or attested.

The Linux mode requires verified little-endian x64/ARM64 statfs/device layouts.
It matches held `fstatfs` device/fsid/type and current-task fdinfo mount IDs with
exact ext4 records in bounded, fresh mountinfo streams; shared ext-family magic
alone is insufficient. Source/root/parent, target/root/parent and typed private
slot must share this mount. Existing root-relative no-cross-mount checks remain.
A held verified procfs/task/fd/fdinfo/ns capability and pinned active mount
namespace are checked around observations and the native transition. The live
operation is synchronous and bound to its actual OS thread; observation/mutation
on another thread refuses. A private keyed, thread-bound authority admits the
low-level Linux lease only inside its dedicated owner. No borrowed target,
state writer, slot or proc authority enters that context.
Native canonical-address and strict xattr paths use `/proc/thread-self/fd`,
without fallback to the process leader's descriptor table. This does not support
arbitrary descriptor closing/reuse races or migration of leases between tables.

The owned facade is `LinuxExposureOwnerContext`. It
accepts immutable reviewed IDs/generations, raw bytes, namespace and target
fingerprints plus private state policy paths. A dedicated OS thread creates all
state/writer/slot, target, source, proc and original-link resources, compares the
reopened authorities, and releases them in its `finally`. The managed proxy has
Ready/Inspect, typed `RetainAsync`/`RestoreAsync`, Completion and Stop/Dispose.
It accepts no borrowed
native objects or caller action delegates. One command can run while at most one
waits, and four owner contexts are the process limit.

Proxy disposal closes managed admission and waits outside native ownership
locks. Proxy finalization only publishes a nonblocking managed stop signal.
Idle and absolute expiry independently close abandoned sessions; owned native
resources remain rooted by the owner until cleanup. Native work drains
cooperatively: a blocked kernel call has no hard wall-clock abort guarantee.
Bounded full exception diagnostics, including flattened callback failures and
explicit truncation/omission counts, return as managed records for caller-side
logging. No caller logger or file stream executes on the native owner.

Per-command outcomes separate `MutationAttempted`, `MutationCompleted`,
`Uncertain`, the observed position and its last-known position. Observations
include detached snapshots of actual original/current link evidence, unchanged
target fingerprints and whether the held typed slot is empty. Legacy metadata
DTOs contain mutable byte arrays: every returned metadata/visible-attribute value,
security descriptor and alternate-stream byte image is copied independently, so
caller edits cannot change owner authority or another observation. This is
detached snapshot ownership, not structural immutability of those legacy arrays. A successful move reply follows both
native namespace reconciliation and the owning state writer/device publication
barrier. Caller, owner-stop and absolute-deadline cancellation use owned sources
with managed callback diagnostics, outside ownership locks. Once rename has
completed, independent bounded reconciliation/publication runs even after caller
cancellation. Such a result remains canceled with `MutationCompleted=true` and
actual retained/restored evidence; it cannot become a no-change report.
Uncertain moves close admission and are never retried.

Cleanup reports `LiveContinuity=false` and the retained/uncertain last position.
Expiry or disposal after retention leaves the native object in its private slot;
it never restores automatically. A closed proxy refuses further moves. This is
a primitive boundary: callers still supply placement/owner policy and a durable
intent before starting either move. No approval, saved journal or process-death
reopen protocol is enforced by this facade.

The primitive marks an attempted rename uncertain before entering the native
call, observes both namespaces and link metadata afterward, then flushes Unix
namespaces. Windows inherits the existing lack of a separate rename-namespace
durability attestation. The final POSIX checked-name to rename interval remains
non-atomic against a same-UID writer; post-move verification can detect and stop
but cannot prevent every competing name change. Any failed/ambiguous attempt
blocks further moves on that lease and retains evidence for external journal
reconciliation. It never retries an ambiguous native rename.

A future caller must durably publish its intent before either move and persist
the original/latest observations and actual locations afterward. This slice
provides no persisted restart/reopen protocol or automatic recovery. Caller
cancellation is checked immediately before native calls; bounded post-move
observation records a completed move before reporting late cancellation. There
is no initial no-op, quota manager, source resolver or gh child in this API.

The live same-object check across rename relies on the still-held original link
pin preventing ID reuse. Process death loses that continuity, and a bare link
has no unchanged child-object witnesses while rename may change its generation.
A future saved reopen must not accept an old ID and raw target while guessing
the new generation of an interrupted rename. It needs independent native
continuity evidence or an explicit ambiguous-state refusal and fresh reviewed
transition; this live primitive does not supply that evidence.

Focused tests use only disposable native fixtures. They distinguish successful
roundtrip from explicit metadata refusal, verify unchanged target bytes/native
objects, wrong-target binding, identical-target link replacement, ancestor
replacement, cancellation and foreign slots. Actual macOS/Windows positive
checks set `SKILLVIEW_EXPOSURE_LINK_PROOF=1` to require supported metadata and
actual retain/restore, including a post-retain restore collision. Linux's
strict mode still requires explicit refusal. The separate Linux tests use
`SKILLVIEW_LINUX_EXPOSURE_PRESERVATION_PROOF=1` to require actual ext4 original-object
round trips and refusals for changed links/targets, collisions and nlink2.
A dedicated-thread descriptor-table divergence fixture runs only when safe
unprivileged `unshare(CLONE_FILES)` succeeds; EPERM is explicitly qualified as
unavailable and supplies no positive divergence evidence. Required subprocess
proof cases instead hard-fail missing support or a missing prebuilt proof host.
They exercise foreign disposal before and after actual retention, initialization
and command failure, finite expiry and abandonment. One creator-private-table
case witnesses actual finalizer-task descriptor addresses against owned leader
sentinels; only that disposable child disables CLR diagnostics to make every
observed owner FD number controllably allocatable. No unrelated runtime descriptor
is replaced. A separate internal friend-proof factory selects one fixed typed
completed-move cancellation checkpoint, then runs real publication; ordinary
`Start` hardcodes no checkpoint. No CLI, environment value or serialized request
selects it. These proof controls are not product switches. JIT test-host native interop evidence
is separate from product AOT/runtime proof. Actual macOS/Windows positive
runtime evidence and four-RID native hosts remain pending. Minimum/current gh
mixed-layout owner/loading/update contracts and the saved detach workflow remain
separate required work; unit/refusal results do not establish those behaviors.

The Windows parser uses Microsoft's documented
[REPARSE_DATA_BUFFER layout](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_reparse_data_buffer)
and the existing verified
[NtOpenFile ABI](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntopenfile).
