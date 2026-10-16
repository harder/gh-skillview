# Saved local reimport

Local reimport replaces one ownerless physical installation from its **same
recorded absolute `local-path`**. It retains the installed native directory for
undo. Every consumer of that physical installation participates. This is a
baseline-less I/C replacement: installed I and captured source C are real
observations; there is no recorded base, Git revision or automatic merge.

Use the separate `reimport-plan`, `reimport-inspect`, `reimport-apply`,
`reimport-recover` and `reimport-undo` family. Apply requires the saved plan
fingerprint and `--yes --disposition replace-local-retain-original`. Recovery
and undo require a fresh inspection fingerprint. The Installed tab's **R**
action uses the workflow coordinator and a modal that owns cancellation and
queued completion. Opening the modal never prepares a candidate automatically.
Enter a saved ID and Inspect to reopen retained history; inspection grants no
replacement approval. Changing the ID or hidden-directory policy closes approval.

Schema 5 uses purpose `local-reimport` and null-omitted `LocalImport`. Revision,
LocalDisposition and PinPolicy must be absent. All lifecycle and journal paths
must validate the purpose and effective owner; schema 1–4 absent-field encoding
and hashes remain compatible. Refuse owner records, mixed GitHub tracking,
pinned policy, a linked final source directory or payload links, source/state/install overlap, partial
inventory, or unsupported metadata. Ordinary ancestor aliases remain bound to the
frozen lexical/canonical mapping and are rechecked before the first retain. The source root must contain a regular
`SKILL.md` with the installed authored name. Payloads containing a nested
`SKILL.md` are refused before child gh staging in this checkpoint: gh also
rewrites nested skill metadata, and that adapter contract remains pending.
Root tracking requires an unambiguous block `metadata` mapping with scalar
members; nested/sequence metadata, flow tracking, aliases and tags are refused
under the existing parser/splice contract. Supported unknown authored fields,
comments, BOM, line endings and body bytes stay captured exactly.

Capture I and C through held native handles into immutable byte and native
metadata archives. The actual minimum/current gh invocation consumes a bounded
private C input using trusted flags before `--`, selects exactly the root name,
and writes only a private destination. Bind its input/output, arguments,
child-only environment, executable version and SHA256 as a staging witness.
Actual gh rewrites YAML presentation and may drop modes and empty directories;
its raw output is distinct from the published candidate. SkillView materializes
captured C and splices only the root `local-path` to the durable recorded source.
Preserve I root privacy and supported C descendant metadata, refusing metadata
that cannot be represented. New active object IDs and timestamps are disclosed;
retain the original native objects for exact undo.

Full source identity/generation, bytes, metadata and lexical/canonical address
freshness gates the **first actual retain move**, including retain-intent repair
when the native topology is still ready. Once I has actually moved to retention,
recovery and undo use frozen archives and journals even if the source changes or
disappears. Inspect reports source staleness separately. Saved archive validation
must not reopen the external source. An intent or step count alone cannot prove
the original moved.

Complete payload, tracking and effective metadata equality is required for a
no-op. Initialize admitted private namespaces before freezing I/C parent
generations; revalidate native leases before archive capture. A no-op may create
an empty final-root directory and preview coordinator, but allocates no retained
operation, opens no final writer and enumerates no retained-plan quota. A bounded held preview runs before retained-plan quota admission; equal
results allocate no retained operation. Changed results copy sealed immutable
evidence into a fresh final operation using held readers and exact raw manifests,
reload the same archive fingerprints, then materialize/recapture the final
candidate. Never rename an active state directory or weaken payload-only native
move admission. Count failed/incomplete previews and final operations, including
duplicate copies until verified cleanup. Unknown child output consumes the
conservative working reservation. Logical byte accounting, child deadlines and
post-run output validation are not filesystem quotas or a hard disk-usage cap.
Uncertain cleanup retains the exact private address and remains charged.

Review the plan ID, plan/review fingerprints, full metadata policy, changed
paths, retained path and all consumers. Require fresh explicit retention approval
for each apply/recover/undo. After any operation, inspect again before authorizing
a recovery action. Cancellation and failure must retain their private evidence
ID/path in reports and logs even if UI delivery is canceled. Reconciliation must
survive UI cancellation and use its own bounded lifetime.

This is a partial M08 checkpoint. Acceptance remains 9/21; all 21 obligations
remain tracked. Native process-death, min/current gh and prior-schema receipts
must name their actual source and host before being claimed as complete.
