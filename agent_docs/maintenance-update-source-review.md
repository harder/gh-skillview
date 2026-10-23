# Frozen source review for saved updates

Product saved-plan factories opt into optional `sourceReview` contract version 1
on schema 1/2/3/5/6. The enclosing plan schema and all absent-field hash domains
remain unchanged. `SourceReview` is omitted when absent. The current reader
accepts existing schema 1–6 plans with their original evidence and admission: legacy plans remain
unreviewed, cannot mint a source receipt without archives, and may still use their
original explicit retention/recovery contract. Low-level legacy/native producers
remain available for those compatibility proofs. Every current source-changing
CLI/TUI product factory opts in; this internal producer choice is not a public guard bypass.
A persisted contract is enforced regardless of the store instance opening it.
Older binaries reject this added field under their strict unknown-field policy;
new contracts must not be routed through an older binary.
Standalone schema-4 unpin changes pin metadata and has no upstream authored-source
review contract. Fresh equal checks retain the existing no-plan/no-write behavior.

New plans capture installed and candidate archives through held native leases,
then analyze only `IPayloadEvidenceReader` byte streams. Native archive copying
retains the original source manifest and metadata when reusing local captures.
The original/candidate payload hashes, full raw archive-manifest hashes, bounded
report hash and captured context have separate domains and are bound by the saved
plan fingerprint. Raw content SHA256 stays in its existing uppercase domain;
saved report/manifest/plan fingerprints use their existing lowercase convention.
No live path is reopened and presented as frozen source text.

Markdown, script extensions (including scripts outside `scripts/`) and
JSON/YAML/TOML configuration text receive literal line replacement hunks. Every
shown changed entry also retains old/new type, mode, size, raw content hash and
link evidence. Root modes remain separate plan fields. Presentation shows at most
128 changed entries, 1,024 diff lines overall, 128 lines per entry and 1,024
characters per line, with explicit omitted/truncated line and entry counts. A
non-text, linked or bounded document receives a summary, never an invented diff.
Both complete captured archive manifests and raw numbered blobs remain beside
`plan.json`; the report names these directories and their exact manifest hashes.
Their existing native bounds are 10,000 entries/256 MiB per archive, with bounded
native metadata. Failed and partial archives remain charged to retained operation
and attempt quotas. Extra review archive names and receipts have an explicit
per-operation bound; unknown state entries still refuse opening.

The bounded declaration scanner is reused on each archive's retained UTF-8
inputs: 128 documents and 8 MiB per side, 2 MiB root SKILL.md and 1 MiB per other
document. Root declarations are parsed by target `SKILL.md`, never blob names.
Each side retains at most 256 observations and 64 coverage reasons, with exact
omission counts. Deltas retain at most 128 rows and explicit omissions.
Unsupported configuration syntax, invalid encoding, links, text bounds and
observation omissions make coverage partial. Literal URL/dependency/tool findings
never imply installed dependencies, effective permissions or actual connections.
`newly-introduced-within-supported-literal-scope` means an exact declaration at
that captured path was absent from complete earlier supported observations.
With partial earlier coverage, the label is `newly-observed` and introduction is
unproven. Arbitrary commands, dynamic endpoints and unmodeled grammars stay unknown.

`update-inspect`, `reimport-inspect`, `source-switch-inspect` and `restore-inspect` expose frozen evidence.
After reviewing it, explicitly declare review with the matching command:

```text
skillview update-ack-source --id ID --expect PLAN_FINGERPRINT --review SOURCE_REVIEW_FINGERPRINT --yes
```

Use `reimport-ack-source`, `source-switch-ack-source` or `restore-ack-source` for those purposes. Keep the same
HOME/state/CWD and scan/workspace/profile options. The immutable receipt binds ID,
plan/report fingerprints, both raw archive manifests, payloads and context. The
acknowledgement verifies all raw archive bytes before publication. Repeating a
matching acknowledgement reads the receipt; it does not overwrite it. A partial
or mismatched receipt refuses. A failed/canceled merge of a legacy plan can retain review archives before the
new contract is published. Reopen admits only strictly named native directories
backed by the loaded local review and an existing bounded merge attempt, with
known regular archive leaves and bounded entry/byte totals. Retained partial
archives confer no review or receipt authority; unknown, unbound, linked or
excessive artifacts refuse. The earlier replacement approval remains closed and
a retry uses a fresh attempt. An unreadable newest merge-plan record cannot revive
an earlier completion.

The declaration is user-supplied review, never
proof of authenticity, compatibility or behavioral safety. Format, agent
compatibility, payload/source integrity and source review remain independent.
The direct local `check` action still reports unreviewed source; merely displaying
a diff/check cannot mint a receipt or transfer review to a same-name installation.

Apply still needs its separate explicit retention/disposition approval. New
contracts require a receipt before apply and every forward recovery, including
never-started plans and retained-intent restart states. A lost receipt can be
recreated by explicitly reviewing and acknowledging the same frozen archives;
source re-resolution is unnecessary. Undo does not require this receipt: its
existing fresh physical inspection fingerprint authorizes restoring the retained
original. Neither acknowledgement nor source review weakens fresh native identity,
owner, local-source freshness, cancellation, or context admission. New merged
candidates get separate archives, report and plan fingerprints; a previous
replacement receipt cannot authorize the merged result. Queued TUI work retains
its operation ownership, and review/retention checkboxes reset on each new result.
The local reimport dialog follows the same declaration and recovery rules.

Native proof scripts explicitly display the frozen report and call the receipt
command before their existing applies/process-kill boundaries. These are declared
fixture reviews that test mechanics; they are not human review or safety evidence.
The legacy-owner proof deliberately retains its actual old producer and checks.
Authored bytes change only through the existing explicitly
reviewed merge/resolution workflow; no silent fixes are added.

The integrated source-switch factory opts schema6 into frozen review. Its native
compatibility harness must also build an actual pre-review schema6 producer from
the verified final PR70 merge. `SOURCE_SWITCH_BASELINE` stays empty until that
merge is known, so this private draft cannot publish a compatibility pass. With
that binding, `prove-old-native-reimport.py --legacy-schema 6` creates a genuine
old plan, preserves its bytes and archives, checks omitted `sourceReview`, kills
the current native apply after retain/install, removes both external sources,
then verifies recovery and exact native undo without inventing an acknowledgement.
Both product hosts run it; Linux uses the existing privileged private-root
container for positive metadata publication. Schema5 remains a separate producer
and proof. Synthetic plans and schema1–5 results cannot replace this boundary.
The container wrapper requires both schema6 producer arguments, a frozen
distinct source commit, and a schema6 result proving source-free recovery; it
cannot report a successful aggregate run after silently omitting that producer.
Detach integration remains separate and must preserve its actual contracts.

PR71 completed the [historical verification](https://github.com/harder/gh-skillview/blob/0f7264f7eca48f6329f9a82d60fbf803e43dd5bd/agent_docs/evidence/maintenance-pr71-m19-m20-final.json).
The reviewed head passed full platform tests, minimum/latest gh contracts and all
four native/AOT lanes before merge. Actual authoring dry-runs and Linux offline
evaluation lifecycles are recorded separately from source-review approval. The
final record qualifies the macOS/Windows schema6 raw-artifact omission; those
summaries are not full raw replay. These maintenance fixtures never execute
payload scripts or establish behavioral safety or model quality. Other original
maintenance findings, including detach integration, remain open.
