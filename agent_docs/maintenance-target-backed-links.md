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
The caller must hold its owning operation's writer lock and complete the owning
writer/device publication barrier after this primitive flushes link-slot
namespaces. Namespace flushing cannot substitute for that operation barrier.

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
tag and bounded valid relative/local-drive substitute paths; junctions, mount
points, UNC/device targets and unknown reparse tags refuse. Invalid Unix target
encoding, broken/non-directory targets and volume changes refuse.
The raw target's final component must itself be an ordinary directory; a link
to another link is refused even if both currently resolve to the held payload.

Only the link is renamed; the target payload is never written. Original and
post-rename identities remain distinct: creation identity/native ID must remain
the same, while change-time/generation can advance. The raw target and metadata
fingerprint remain equal. No unlink/recreate, overwrite, copy/delete, archive
restoration or cross-volume fallback exists.

Full link metadata is a mutation gate. macOS/Windows reuse existing bounded
capture and verification, including supported attributes or full Windows
security/backup streams; unavailable privileges or unsupported observations
refuse. Linux capture remains useful read-only evidence, but mutation always
refuses under the current adapter: `CapturePayloadMetadata` reports both any
missing trusted-xattr visibility and unobservable symlink inode flags. Even
CAP_SYS_ADMIN does not remove the inode-flags refusal. This slice neither guesses
an ioctl/statx interpretation nor treats preservation of an opaque inode as
proof of complete metadata observation. A future Linux investigation needs a
verified way to observe the missing flags, or a separately reviewed narrower
preservation-only contract; it must not erase current refusals.

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
actual retain/restore, including a post-retain restore collision; Linux still
requires explicit refusal before mutation. This environment value affects test
assertions only and is not a product switch. JIT test-host native interop evidence
is separate from product AOT/runtime proof. Actual macOS/Windows positive
runtime evidence and four-RID native hosts remain pending. Minimum/current gh
mixed-layout owner/loading/update contracts and the saved detach workflow remain
separate required work; unit/refusal results do not establish those behaviors.

The Windows parser uses Microsoft's documented
[REPARSE_DATA_BUFFER layout](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_reparse_data_buffer)
and the existing verified
[NtOpenFile ABI](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntopenfile).
