# Saved local dispositions

New local-merge creation and the built-in editor are retired. The CLI no longer
routes `update-merge-review`, `update-merge-choose` or `update-merge-plan`.
Ordinary source/local-file comparison, preserve/skip and reviewed replacement
with the original retained remain available. `update-preserve` independently
verifies the exact never-started plan's installed payload and owner; it writes no
payload, owner or maintenance preference.

## Retained schema-3 boundary

Deleting the creator does not retire its persisted formats. Keep schema-3 plans,
`LocalDisposition`, schema1/2 choice records and resolution manifests unchanged.
Absent optional fields continue to omit themselves from earlier schemas and
hash inputs. The existing update inspect/apply/recover/undo dispatch remains.
Apply requires the exact saved `merge-reviewed` disposition and plan fingerprint;
recovery/undo require a fresh observation fingerprint. All native gates remain.

Saved B/I/C byte and metadata archives, selections, resolved-file references and
result M must validate together. C remains the real future comparison baseline;
M has separate exact result evidence. Custom-resolution input paths stop being
dependencies after capture; immutable private blobs are read and verified.
`ThreeWayPayloadReview`, `PayloadMetadataReview` and resolution readers remain
necessary validators, even though no current product API creates a merge.

Bound resolution storage including failed/unreferenced attempts: at most 16
archives, 256 MiB of raw bytes and 64 MiB of encoded manifests per plan. Bound
the draft scan before counting. Invalid newest choices or partial attempts must
never revive older approval. Count immutable `merge-plan-NN.json` records as well
as current `merged-NN` slots: promotion vacates the staging slot. A partial initial
review still closes the old replacement approval. Keep incomplete evidence for
inspection rather than granting replacement or silently discarding it.

Native metadata policy still preserves compatible installed attributes, captured
source/resolution attributes on new nodes and reviewed installed-parent ownership.
Unknown metadata, unsupported ACLs, external hard links, flags, stream layouts and
incomplete namespace coverage refuse admission. Retained native originals remain
the rollback authority. Legacy capture omits Unix link modes; metadata capture
includes them. Keep those hash domains separate.

## Verification

Historical schema-3 proofs remain current coverage while the schema is supported.
`prove-old-native-plans.py` retains real old-host creation/current-host process-kill
recovery and exact undo. `prove-local-merge-update.py` and the shared-owner merge
mode use the last creator (`27efe94ddbde31ef3c8e6e7dbbee41c00c8b7177`)
only to author disposable source-reviewed schema-3 fixtures, including captured
custom resolutions. Assert the exact source-review shape and prove current apply
refusal before acknowledgement, then acknowledge and apply/recover/undo. The
older `0dba59be` producer remains separately bound to the pre-source-review shape. Current
hosts still perform preserve, inspection, disposition refusal, apply, crash
recovery, disk-full recovery and undo. Verify the producer's exact clean checkout
or transferred source/binary receipt; never silently use the current product as
a creator. The Docker disk-full lane stages that producer read-only too.

Managed tests use `HistoricalMergeFixture`, restricted to flat regular files and
empty directories. It exists only in the test assembly and grants no production
creation API. Creator-only editor, interactive choice, capture retry and merged
materializer tests are retired. Saved reader, schema/hash, native metadata,
source-review fallback, shared owner, cancellation, recovery and undo checks stay.
Shared metadata clone tests use `InstalledPayloadMaterializer`, which remains
required by unpin and local reimport. See [native metadata](native-payload-metadata.md)
and [saved updates](../docs/saved-updates.md).

The text captures `updates-review-80x24-review.txt` and
`version-selection-80x24-review.txt` are historical evidence linked only by
historical notes. They retain their original removed-control labels and are not
current product guidance; this deletion does not regenerate historical captures.
