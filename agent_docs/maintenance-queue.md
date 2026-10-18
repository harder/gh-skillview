# Maintenance queue and local plan references (M20)

`MaintenanceQueueBuilder` projects reconciled inventory and the existing cleanup
classifier into read-only issue + physical-installation rows. Use the frozen
`OperationSubject`, installation address ID and affected paths throughout. The
queue never authorizes mutation or establishes current object identity. Empty
directory issues retain the classifier's captured subject. Unknown update and
active-session state must remain explicit.

Changes routes the selected cleanup issue through a fresh classification and
filters by issue, exact path and installation ID. Updates drill-in resolves the
selected ID + path against fresh inventory, clears all/batch choices, and shows
only that installation. Preserve that restriction on refresh. Missing/ambiguous
selection yields no target; never fall back to a name, sibling or row index.
The selected Updates row's `c` shortcut prepares one immutable specialist plan
and displays installed→candidate file hashes, types, byte counts and modes. Its
bounded preview omits whole fields and reports omissions; full comparison evidence
is in plan JSON. This shares `u`'s existing coordinator and apply/reconciliation
path, including fresh retention approval and sequential sibling preparation.
Preparation may resolve/stage an explicitly requested remote source; entering or
refreshing the queue does not. No skill code or client session runs for comparison.

The presentation retains lightweight rows and renders only the selected detail.
It caps visible rows at 8,192, selected detail at 32 Ki characters, and shared
coverage detail at 8 Ki characters. Omission counts are explicit; exact fields
are omitted whole rather than shortened into routing selectors. Full schema JSON
remains available through a fresh queue capture. Projection and field rendering
check cancellation while enumerating. Coverage text is retained once per view.

Selected cleanup hand-offs use the Changes activation/request lease, not only
the app lifetime. Leaving or refreshing Changes cancels pending selection work;
repeated Enter supersedes a pending capture. The queued callback rechecks the
request before admission. Once dispatch starts, ownership remains exclusive
until the awaited modal/UI completion returns, so repeated activation cannot
admit another modal. Create the request inside admitted owned work to avoid
leaking a lease when shutdown rejects task admission.

`queue [--install-id ID] [--allow-hidden-dirs] [--json]` refreshes local/catalog
inventory without update checks, skill evaluation, saved-plan preparation or
agent startup. JSON schema 1 retains full frozen subjects, exact affected paths,
coverage diagnostics and explicit policy/status distinctions. Exit codes are
0 for captured observations, 1 for partial inventory, 2 for invalid arguments,
and 20 for a complete capture with no matching observations. Entries are
observations rather than confirmed updates. Re-run `queue` or use Changes `r`
to refresh disk inventory; checking availability is a separate explicit action.

`inspect --path/--install-id` selects one physical installation using the existing
exact-selection contract. `check` remains the bounded static package checker;
availability remains the explicitly requested `update --dry-run` specialist.
`plan --purpose update-revision|restore-recorded|unpin-only|local-reimport|switch-local-source` routes
to the existing specialist planner. Its JSON response includes a schema-1
`planReference`, identifying the locally retained plan ID, plan/context fingerprints,
purpose, installation ID and original path. It contains no payload authority.
Source switching additionally requires `--new-source <absolute directory>` and
its `replace-local-source-retain-original` retention disposition.
Plan and inspection envelopes can be redirected to a file; a bare `planReference`
object is also accepted. Limit: 256 KiB UTF-8 JSON, depth 32, regular native file,
no duplicate reference members, unknown reference fields or arbitrary archive paths.
Larger envelopes need their `planReference` object extracted. Capture uses
`RegularFileInspection.Capture`, including held-object and final content checks.

`inspect|verify|apply|repair|undo --plan-file FILE` reread that bounded file once,
then compare the entire locator with a fresh native-store inspection. No context
field in the file is used to construct inventory or state locations. The native
store still checks purpose, immutable records, live slots, owner locks/receipts,
local-review closure and native identity. Apply independently requires `--expect`,
`--yes` and the specialist retention disposition; file text never supplies consent.
Repair resumes only an existing recognized interrupted transaction with a current
inspection fingerprint. It refuses a ready plan rather than starting replacement
without retention consent. A ready topology with an interrupted pre-move journal
requires explicit specialist recovery review. Torn owner-image repair stays the
explicit specialist action; generic repair never infers an owner transition.
Legacy quarantine `undo --id` remains separate.

These fields are transient projections (`JsonIgnore` on `ReplacementResult`), so
persisted plan/result hashes and schemas remain unchanged. A merged plan needs a
fresh specialist inspection/export because its fingerprint changes. A source-review
acknowledgement is independent of the reference and retention approval. Generic
apply enforces the same persisted receipt as specialist apply; declare the exact
frozen source review with its purpose-specific `*-ack-source` command first.

Queue JSON and operation output distinguish affected and retained exposures from
retained payload locations. Reload instructions are separate from disk verification.
For observed `claude-plugin`/`local-claude-plugin` deliveries to Claude, official
[plugin documentation](https://code.claude.com/docs/en/discover-plugins#keep-plugins-updated)
checked 2026-10-16 says background disk updates leave an existing session's loaded
version in place; `/reload-plugins` or a new session applies it. This is a conditional
instruction, not a failure or an observed runtime fact. The official
[Codex catalog](https://github.com/openai/skills#installing-a-skill) instructs restart
for newly installed skills; it does not prove what an existing session loaded.
Other agent/delivery combinations retain an explicit unavailable reload reason.
Client versions/surfaces can differ; guidance does not launch or inspect a client.

PR71 completed the [original M20 acceptance](evidence/maintenance-pr71-m19-m20-final.json)
after exact-head platform/native checks and merge. The queue does not retain
availability results across a later disk refresh;
unsupported owner transitions, disablement and migrations remain specialist/refused
actions, not invented generic capabilities. Native platform behavior remains
subject to the existing backends' supported and explicitly refused cases.
No session or loaded-version fact is inferred from inventory or a read-only client
list handler. The original audit clauses remain unchanged; other maintenance
findings retain their separate incomplete status.
