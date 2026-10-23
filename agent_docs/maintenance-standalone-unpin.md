# Standalone unpin

Schema4 `unpin-only` changes the observed installer pin while preserving the
installed authored payload and its existing recorded repository/ref/tree. It
never resolves a source, stages a remote tree, invents a commit or represents the
installed customized tree as a pristine baseline. `Revision` and `LocalDisposition`
are absent. Older schema1–3 omit `PinPolicy`, preserving their saved hash inputs.

`unpin-plan --path PATH` or `--install-id ID` freezes the exact installed native
payload, complete exposures/context, a retained installed archive, supported
native metadata policy, desired clone and optional matching shared-gh v3 owner
entry transition. `unpin-apply --id ID --expect HASH --yes --disposition unpin-only`
authorizes that saved result with explicit retention. `unpin-inspect`,
`unpin-recover`, `unpin-undo` and `unpin-repair-owner` use the same durable ordered
payload/owner journal as saved replacements. Recovery/undo/repair require a fresh
inspection fingerprint. Command families and store purpose guards prevent
update/restore from interpreting an unpin plan or the inverse. Agent-session state
remains unknown.

The YAML writer removes one complete unambiguous `metadata.github-pinned` member
by strict UTF-8 byte offsets. All other raw bytes, including BOM, line endings,
comments and other tracking values, remain unchanged. It reparses and compares
all flattened authored fields and metadata with the one pin removed. Independent
root `pinned: true`, duplicate keys, anchors/aliases/tags, flow mappings/sequences,
block scalars and a sole-child metadata mapping are explicit refusals. A shared
owner changes only its `pinnedRef` JSON member and an adjacent separator, retaining
all other selected-entry bytes, timestamps/extensions and unrelated current owner
entries. Schema4 refuses even semantically equivalent selected-entry reformatting
after review, so it cannot overwrite fresh unrelated raw member bytes. Owner authority stays on the same native file object under gh's DATA FILE
lock; repair remains a separately reviewed whole-image operation for recognized
torn writes.

Archive capture and native move evidence use different comparison representations:
strict `CaptureInto` retains the normalized authored SKILL.md comparison hash and
Unix link modes; `CaptureEvidence` stores raw comparison hashes and omits link modes.
Schema4 projects those representation fields when binding the archive to native
original/candidate evidence. It still compares complete raw entries, root mode,
full native identities/generations and raw/object fingerprints. Immutable archive
hashes and schema1–3 projections remain unchanged.

The existing native directory swap supplies the authority and durable recovery
boundary. Applying creates active native objects and regenerates timestamps while
preserving exact path sets, authored file bytes, extra files, local deletions,
empty directories, modes, admitted link bytes/kinds and supported native
attributes. The saved review discloses this tradeoff. The actual original native
directory is retained, so undo restores its original objects and payload rather
than reconstructing them. Unsupported or unknown metadata refuses publication;
ordinary Linux without proven trusted-xattr namespace authority is a concrete
refusal, not a successful native proof.

An already-unpinned result requires complete inventory/root/owner admission and
fresh native payload/context checks before returning. It opens no private state
writer, enumerates no retained-plan quota and resolves no source. A full private
quota cannot turn that verified no-op into a plan. Once a private operation is
opened, planning refusal/cancellation reports its exact retained ID/path and
underlying reason without removing partial evidence. Missing, partial or inconsistent
owner/pin evidence does not establish an already-unpinned state.

This operation applies to the complete physical installation and displays every
affected exposure. It does not implement per-consumer unpin/detach, durable forks,
source switching, local reimport, other native owner adapters or preference
recovery. Those broader workflows remain outside standalone unpin and require separate scope approval.
