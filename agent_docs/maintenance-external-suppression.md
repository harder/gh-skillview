# Original M08 continuation after M12

Do not mark M08 complete after this first slice. Original M08 requires preserve/merge,
fork, replace-with-backup/cancel, source and other owner transitions, external
suppression, distinct restore/current/latest/unpin, and original acceptance proof.

First reviewable slice: external cleanup suppression preferences.

Current issue: CleanupScreen.DoIgnore writes .skillview-ignore inside the selected
payload synchronously on the UI thread. That alters manager-owned payloads and can
make later whole-payload comparison/update report an artificial local edit.

Proposed behavior:
- Retain read-only recognition of legacy .skillview-ignore; never create/remove
  payload markers through ordinary Ignore actions. Legacy removal/migration must
  be a separately reviewed action, not a silent inventory mutation.
- New preferences under the existing context-aware private SkillView state home,
  in a distinct preferences subdirectory, never in replacement-plan storage.
- Explicit exact-install suppression and clear CLI actions with schema-versioned
  JSON; state is path/source-context specific, not skill-display-name based.
- Persist dedicated canonical physical address plus observed stable source and
  owner/context binding. InstallationId is only an address hash and cannot prove
  object/source continuity. Known-source revision/tree/pin/distributor version
  changes do not alone invalidate the binding. Ownerless/uncertain source is
  explicitly native-object-and-whole-payload-bound and becomes inactive after
  changed or uncertain evidence. An unchanged directory inode does not establish
  source continuity when files were overwritten in place. Rehash only referenced
  ownerless candidates within an aggregate bounded budget off the UI thread;
  budget/access failures leave suppression inactive with diagnostics. Aliases
  resolve to this binding, never display-name matching.
- Routine inventory reads are bounded and noncreating. Unreadable, malformed,
  unsupported or partially written state produces diagnostics and does not
  silently suppress action recommendations. Legacy markers remain visible as a
  distinct origin, and clear-external cannot claim to clear legacy suppression.
- Durable publication uses held native private directory authority, independent
  writer lock, size/entry quotas and integrity/schema validation. Prefer immutable
  sequential records, published only after complete bytes+checksum; recover a
  torn tail without accepting its preference. Bound total files, reserve enough capacity to clear every active preference
  before admitting a new set, and perform no best-effort destructive pruning in
  this slice. Readers cannot require writer creation.
- Inventory attaches preference outcome after physical merge while preserving
  provider diagnostics/coverage. CLI list/cleanup explain suppression origin;
  comparison payload hash excludes no new files because none are added.
- TUI Ignore runs owned background work, snapshots exact checked subjects first,
  shows per-target failure/skip/cancel accounting, drains before disposal, rescans
  only when preference state changed. Never write through a captured string
  after current source/context binding has changed.

Proof: actual gh-owned fixture + ownerless fixture, alias and same-name sibling,
unchanged all payload bytes/native identities/modes/registry before and after
set/clear; restart persistence, missing-state read creates nothing, corrupt or
truncated state, concurrent writer, already-canceled admission, malformed schema,
quota bounds, alternate HOME/SKILLVIEW_STATE_HOME, source replacement at same path,
legacy marker remains unchanged and origin visible. Local + all platform suites,
both native hosts/all four RIDs and independent review before merge.

Then continue full M08: immutable approved-source staging and whole-payload
three-way review; explicit preserve/merge with conflict reporting, fork/retained
ownership policy, and source/native-owner transitions in saved recoverable plans.
Do not alter old persisted journal schema/hash inputs without actual old-native
compatibility tests. Preserve existing no-overwrite admission until each new
transition/disposition is implemented and proven.

Source read-through refinements:
- InventorySnapshot.IsPartial derives from physical provider failures. Preference
  diagnostics are a separate frozen observation, not a fake failed physical
  provider; an unavailable preference file must not manufacture absent skills or
  silently invalidate unrelated exact update/removal authority.
- Attach a transient per-skill SuppressionObservation (external/legacy origins,
  binding mode/status, record sequence, diagnostic). Keep old persisted subject,
  metadata and replacement-context hash inputs unchanged. Explicit operations
  continue to identify exact targets; ignoring a cleanup suggestion never grants
  mutation rights or forces routine update admission.
- CleanupClassifier also emits non-skill entries. Capture native parent/name/link
  identity for broken links, and empty-directory identity/content preconditions.
  If a binding cannot be verified, the batch reports an explicit skip/failure;
  no checked row silently disappears from accounting.
- Existing cleanup modal currently performs Ignore synchronously and refreshes
  only on IgnoredCount>0; replace this ownership path so partial preference writes
  plus cancellation still trigger a rescan and remain visible in results.

Binding and UX decisions for implementation:
- Cleanup suppression applies to one physical installation, across its displayed
  consumer exposures. Say so before a filtered view sets it. It is neither an
  agent disable switch nor a desired-update policy. New aliases of the same
  verified physical/source binding share this preference; same-name other copies
  do not. Changed/added owner contexts make the binding inactive for review.
- Known-source binding includes canonical address, stable normalized source and
  subpath, sorted owner-provider/registry/selector/context namespaces; excludes
  revisions, tree/pin/content hashes, delivery versions and capture timestamps.
- Unknown-source directory binding includes native object ID/generation, complete
  bounded payload fingerprint and root mode. Inventory rehashes only referenced
  unknown-source candidates and reports explicit inactive/budget diagnostics.
- A broken-link preference binds canonical parent ID plus final name and the
  native link ID/generation/raw target bytes. Parent generation is revalidated
  during writes but need not invalidate a later read solely because an unrelated
  sibling changed. A new parent object or replaced link must invalidate it.
- Prefer an existing noncreating writer-lock open for a stable bounded read;
  temporary contention yields a visible retryable preference diagnostic and
  leaves recommendations visible. No read may initialize state or cache an
  unavailable result as permanently empty.
- Storage integrity is an explicit record checksum, not a security/authenticity
  claim against another process running as this user. Invalid/unrecognized latest
  records leave relevant suppression inactive rather than silently reviving an
  earlier set. Keep incomplete files for inspection. Do not overwrite a torn
  create-new filename. No-op set/clear appends no records.
