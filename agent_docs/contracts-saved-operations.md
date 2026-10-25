# Saved-operation contracts

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

- Product source-changing saved plans bind optional version-1 source-review
  evidence and an explicit immutable acknowledgement separately from retention.
  Analyze held immutable archives, never reopened live paths; enforce persisted
  contracts even through legacy store instances. Legacy absent fields retain old
  hashes and stay unreviewed. Pin-only changes are distinct. See
  `agent_docs/maintenance-update-source-review.md`.

- Maintenance queue rows are frozen observations, never mutation authority.
  Route by exact issue/path/installation ID against fresh inventory and refuse
  missing or ambiguous selections. Preserve the single-install Updates drill-in
  restriction across refresh; never fall back to a same-name sibling. Disk
  refresh does not check upstream availability or observe running sessions.
  Keep selected hand-off ownership through awaited UI dispatch; retired Changes
  requests cannot open a modal, and started dispatch stays exclusive until return.
  See `agent_docs/maintenance-queue.md` for the queue workflow and its limits.

- Maintenance plan files are bounded local references, never imported payload
  authority or approval. Read them through `RegularFileInspection.Capture` and
  match ID/purpose/fingerprints/address/context against the native retained store.
  Keep independent `--expect`, explicit retention, source-review and native
  transition gates in specialist workflows. Generic repair cannot start a ready
  replacement. Disk verification and conditional agent reload instructions never
  establish active-session state. See `agent_docs/maintenance-queue.md`.

- Ordinary CLI/TUI updates use `SavedUpdateCoordinator`: prepare one immutable
  candidate, review, then apply its saved ID/fingerprint with explicit retention
  approval. Only availability checks use the low-level mutable gh update adapter
  in dry-run mode. Never prepare every sibling before applying the first: native
  parent/root generations change. Keep the next preparation after prior apply
  reconciliation, which must survive canceled view/UI dispatch. Availability
  checks cannot consume retained-plan quota. See `docs/saved-updates.md`.
  Result-view undo uses `SavedUpdateCoordinator.InspectUndo` and `UndoAsync`:
  inspect the exact successful revision update, bind its current observation
  `ReviewFingerprint`, then require separate restoration approval. Never reuse
  the immutable apply `PlanFingerprint` as undo approval. Every undo attempt
  closes the UI approval; reconciliation must survive canceled UI delivery.
  Evaluate undo eligibility from the current apply result, retaining aggregate
  batch diagnostics separately. Distinguish native restoration from incomplete
  post-attempt inventory verification; neither implies the other failed.

- One-time version choice uses the existing schema-2 saved update path for an
  exact Git tag and one physical installation. Never fall back to latest or
  forward tag options to mutable gh update adapters. Existing pins require a
  separate unpin review. TUI version choice targets the current row regardless
  of marks/All; every shared consumer remains affected. It creates no lasting
  version policy: later ordinary updates still follow latest.

- Local source switching uses schema6 `switch-local-source`, a separate
  `LocalSourceSwitch` placement/history observation and explicit
  `replace-local-source-retain-original` disposition. Keep schema5 exact.
  Prove old-source canonical placement or native first-missing boundary before
  private writes; unknown resolution must not use lexical fallback. Linux
  placement follows ordinary ancestor aliases with `openat2` NO_XDEV, refusing
  bind/cross-filesystem transitions rather than comparing namespace strings alone.
  Old content
  is never a base/freshness dependency; after actual retain, neither external
  source is required for frozen recovery. See
  `agent_docs/maintenance-local-source-switch.md`.

- Local reimport uses schema5 `local-reimport` with `LocalImport`, omitting
  Revision/LocalDisposition/PinPolicy and preserving schema1–4 hashes. The same
  recorded ownerless local source must be fresh before the first actual retain;
  subsequent recovery/undo uses frozen archives even when that source disappears.
  Promote previews by held immutable copies into a fresh operation, never by
  renaming active native state. See `agent_docs/maintenance-local-reimport.md`.

- Standalone unpin uses schema4 `unpin-only`: bind actual installed capture and
  pin policy without fabricated revision/B/C evidence. Remove only verified raw
  pin members; retain recorded source/ref/tree and all physical exposures. Use
  the checked effective-owner accessor throughout saved lifecycle/journals.
  Schema1–3 omit `PinPolicy` to preserve existing hashes. Verify already-unpinned
  before private state writers or quota enumeration. Review the native swap's
  new active object identities/timestamps; retain original native objects for
  exact undo. See `agent_docs/maintenance-standalone-unpin.md`.

- Saved revision updates use schema 2 replacement plans, retaining the recorded
  base and explicit local-change/pin review. Keep schema 1 recorded-restore
  fingerprints compatible by omitting absent revision fields. Registry writes
  must hold gh's DATA FILE lock through payload and owner transitions and keep
  the same file object; replacing its pathname breaks coordination. While a
  native registry lease is held, read through its handle (Windows byte locks
  reject second-handle reads). NativeStateDirectory publication requires its
  `writer.lock` handle to remain open. See `agent_docs/maintenance-update-plans.md`.
  New local-merge creation/editor is retired; existing saved local merges retain
  separate schema 3 plans and immutable B/I/C byte+
  metadata archives; C remains the real source baseline while M has its own
  exact result evidence. Omit absent disposition fields from schema 1/2 JSON.
  A retained historical local review closes the original replacement approval,
  including partial capture. Private attempts stay bounded and retained; derive
  history from immutable plan records as well as staging slots because promotion
  vacates the latter. A newest partial attempt cannot revive older completion.
  Keep these readers and validators for inspect/apply/recover/undo. See
  `agent_docs/maintenance-local-dispositions.md`.
  Linux metadata enumeration cannot infer complete trusted-xattr visibility
  from namespace capability bits. Its permission witness is a fresh held
  `O_TMPFILE|O_EXCL` inode, never a retained filename; unknown coverage refuses
  publication. macOS can retain `com.apple.provenance` after a successful
  `fremovexattr`. Verify exact attributes after every staged metadata change;
  preserve compatible I attributes and copy captured C attributes on new
  source nodes, separately from reviewed I-parent owner/group inheritance.
  Saved custom resolutions use `IPayloadEvidenceReader`, not synthetic root
  snapshots. Schema2 choices bind exact completed resolution manifests; schema1
  omits that field. Keep original source evidence and charge incomplete and
  unreferenced attempts to per-plan quotas; bound the draft scan first. Shared
  native metadata primitives remain required by unpin and local reimport.
  Unix registry owners must explicitly `LOCK_UN` before closing, including
  failed initialization after acquisition: a fork can retain the open file
  description temporarily even with CLOEXEC. Duplicate-descriptor regressions
  must use the verified `DupWithRetry` (`F_DUPFD_CLOEXEC`) helper.
  State writer streams likewise explicitly unlock before close, for synchronous
  and asynchronous disposal. Do not replace them with plain FileStream writers;
  duplicate descriptions can otherwise retain a completed operation's lock.

- Payload equality and native object continuity are separate evidence.
  InstallationId remains an address identifier, including across native object
  replacement at the same path. Inventory captures `OperationSubject` off the
  UI thread; render `OperationImpact` from that frozen evidence through refusal,
  failure and cancellation. Link-only impact includes every context of the
  canonical link entry. Do not infer a commit from a tree or authored version.
  Transient identity projections must stay out of saved subject/owner JSON and
  its hashes; verify actual old-native restart compatibility when changing this
  area. See `agent_docs/maintenance-identities.md`.
  `PayloadSnapshot.ObjectStateFingerprint` retains full native IDs/generations
  for the root and every observed leaf/directory, plus observed Unix modes.
  Backup capture rechecks both raw bytes and this evidence before publishing a
  captured journal. It is not a full ACL/xattr/alternate-stream attestation;
  metadata-aware recovery needs its own admission/revalidation contract.
  Root Unix modes are reported separately from the schema-1 archive fingerprint
  so old archive/history verification remains compatible.

- Current maintenance contracts and historical evidence are indexed in
  `agent_docs/README.md`. A name collision or absent gh
  catalog entry is never sufficient removal evidence. Preserve explicit root
  coverage, authored-version versus repository-ref semantics, and all agent
  exposures when reconciling inventory. Frontmatter uses bounded YAML events;
  do not replace it with reflection-based deserialization or alias expansion.

- Exposure-link retention uses its dedicated target-backed typed-slot primitive;
  never relax directory/state-file admission or replace retained links by recreation.
  Full-metadata Linux symlink inode-flags refusal remains unchanged. Its separate
  explicit same-original-object mode requires a live original pin, one verified
  ext4 mount and current-thread proc/namespace evidence; hidden metadata stays
  unknown. Linux admission is only through the dedicated owner context, never
  the borrowed-target overload. It creates/uses/releases every native authority;
  a foreign Dispose/finalizer only signals managed stop, never closes/unlocks FDs.
  Publish completed moves through the owning writer barrier before replying,
  including canceled moves. Report actual position/uncertainty and loss of live
  continuity after cleanup; retained expiry never automatically restores.
  Never use that narrower evidence to admit clone/archive/materialization.
  Windows dot-containing raw targets use explicit-base normalization only for
  a read-only candidate witness from the original held parent. Require the
  opened full native target identity/generation/address and bracket that parent;
  never substitute this path for held-relative rename/delete authority.

- Standalone quarantine/undo uses a same-volume native exclusive directory rename,
  prepared/completion journals, and held root/parent/object evidence. Keep it limited
  to explicitly reviewed standalone custom-directory copies; never add archive
  extraction, overwrite, cross-volume copy/delete, or automatic ambiguous recovery.
  Resolve discovered exposure destinations even when links are dangling, relative
  or aliased, and check subtree overlap at source, retained and original addresses
  before publishing prepared journals. Include link entry addresses as well as
  intended destinations, and refuse configured/scanned roots inside a relocation
  boundary even when empty or expected absent. See `docs/quarantine-and-undo.md`.
  Quarantine retains live ACLs/links and does not
  isolate content or attest agent session state.

- Fresh equal update checks leave installed objects and owner bytes/timestamps
  untouched and consume no saved-plan quota. Clean only their never-published
  private stage through ownership duplicated from its held state handle; failed
  cleanup must expose the exact location. Legacy equal plans may return unchanged
  only after fresh ready-state validation; interrupted moves still need recovery.
  Validate persisted schema-2 owner transitions against exact fresh or historical
  serialization; no-op preparation changes must not strand older recovery plans.
  Preserve bounded stdout/stderr and earlier mutation/failure evidence across
  the captured update queue without reusing its prior Apply target. See
  `agent_docs/maintenance-update-outcomes.md` for the contract and proof commands.

- Ordinary Cleanup Ignore and CLI `ignore` write only the private sibling
  `SkillView/preferences` store; never write or silently remove legacy
  `.skillview-ignore` payload markers. Keep suppression observations transient
  (`JsonIgnore`) so old maintenance journal/hash inputs remain unchanged. Unknown
  source writes must compare reviewed, freshly inventoried, and execution-time
  native object/generation evidence, then bind the complete bounded payload.
  Charge failed captures against the aggregate inventory byte budget. Preference
  diagnostics are independent from physical-provider inventory coverage. Keep
  cleanup confirmation dialogs on the event-loop thread before worker admission.
  Include preference-store construction in its diagnostic boundary so missing
  HOME/USERPROFILE cannot abort inventory. Incomplete ownership diagnostics block
  suppression capture/activation; external clear still bypasses binding capture.

- Product updates use `TuiServices.SavedUpdates`, carrying physical targets
  rather than names alone. Keep `--dir` rooted at the verified containing skills
  directory and gh noninteractive confirmation separate from target selection.
  Local drift refuses replacement even with force until an explicit recoverable
  repair disposition exists. Preserve both streams and post-attempt verification
  on failure/cancellation. See `docs/saved-updates.md`.

- Compare revisions by evidence domain. An authored version, Git tree, owner
  payload hash and distributor version are distinct facts; equivalent Git-tree
  evidence must not become incomparable just because one copy also has a lock
  claim. Missing evidence is unknown. Recheck contextual record fingerprints
  before recommending a migration, and suppress it when coverage is partial.

- Recorded restoration uses `ReplacementPlanStore`, separate from standalone
  quarantine admission. Promote the saved candidate; never rerun a moving remote
  update after review. Keep source/tree/pin and owner entries unchanged for this
  workflow. Hash plan JSON canonically, sorting object properties: immutable
  dictionary enumeration can differ across processes. Capture the installation
  ID once; path key casing can change while its original slot is vacant.
  Bind live parsed tracking to the held original's raw and native fingerprints
  before staging; inventory metadata can change before planning begins.
  A namespace parent may appear as MissingSkillMd while its only child is
  retained, and that row can remain in a caller's stale inventory after restore.
  Recognize only the saved native parent without its own skill/Git/observed
  ownership. Ordinary README files remain outside the moved child. Use canonical
  parent addresses with native root-relative validation, and reread owner records
  for the physical namespace installation root as well as discovery roots.

- Replacement transitions rescan currently existing configured roots, including
  previously missing roots, and reread contextual owner records before moving.
  Partial step journals are retained as non-authoritative evidence; explicit
  recovery binds reviewed live slots. Reserve intent/completion space before a
  move and retain the separate bounded undo journal for exhausted forward logs.
  Keep its writer handle alive for native publication barriers. Flush staged
  files and directory namespaces before publishing a ready plan. See
  `docs/recorded-restoration.md` and the real-gh replacement proof.
  Windows can report `Directory.Exists` for a dangling directory symlink. State
  exclusion pinning must accept only native file/path-not-found from the target
  open as absence, retain the excluded address, and propagate access/sharing
  failures. Keep shared-link restart coverage for both vacant-original slots.

- Saved shared revision updates hold gh's native data-file lock across payload
  moves and selected-entry publication. Use same-handle lockfile reads (Windows
  byte locks prohibit a second-handle read), preserve unrelated raw bytes, and
  bind recovery review to the full registry snapshot. Torn-image repair is a
  separate explicit action authorized by the latest durable unfinished owner
  write and bounded image ancestry; never replay arbitrary historical preimages.
  Keep missing/partial main and raw intent publication, live compensation, and
  actual process-kill restart proofs. See `agent_docs/maintenance-update-owner-transitions.md`
  and the proof-only host instructions in `agent_docs/running-tests.md`.
  Persist the held registry parent's full native identity/generation across
  released leases; file IDs alone cannot distinguish Unix inode reuse from
  permitted in-place data edits. Initialize validated private state ancestors
  before capturing that witness when state is beneath `.agents`; never refresh
  it after staging to accept an unexplained namespace change.
  Windows additionally pins a bounded immediate namespace fingerprint from the
  held parent opened with enumeration access: names, full IDs, creation times
  and entry types. Child content, ChangeTime and reparse payload are excluded so
  in-place registry writes and moves beneath `skills` remain valid. Parent
  ChangeTime alone can miss a sibling addition/removal/replacement; test those
  cases with timestamps explicitly held equal, alongside a distinct-generation
  create/delete case and successful ordinary writes.

- Retention output must distinguish observed directories from broken links and
  unknown historical materialization. Freeze the subject before work; only a
  verified capture or recorded transition may strengthen its retention evidence.
  An updater exception after mutation admission requires bounded verification,
  inventory invalidation and conservative mutation accounting. Failure callbacks
  must retain the requested operation (for example, `undo`).

- Cleanup cancellation and unexpected failure must preserve duplicate skips and
  exact partial mutation totals through both progress throttles. Accumulate one
  terminal report per attempt, including failed attempts, so workflow rescans
  cannot miss a deleted file. Keep retry and omitted-attempt-state coverage.

- CLI removal orchestration must convert unexpected removal faults into a failed
  subject-bearing result using captured progress, with one added error. Preserve
  earlier cleanup rows and continue independent targets; low-level removal APIs
  still throw, and unrelated cancellation must not be labeled owned cancellation.
