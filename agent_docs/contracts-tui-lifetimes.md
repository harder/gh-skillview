# TUI and asynchronous lifetime contracts

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

- `ActionLabels` supplies pure display metadata for contextual Help, shell hints
  and corresponding controls. Add labels for existing handlers only; routing,
  text-input guards, ownership and mutation approval remain in those handlers.
  Keep review-screen aliases distinct from primary-tab actions.

- Prefer the built `skillview` host binary over `dotnet run` for PTY-driven TUI
  automation. `dotnet run` can add first-run noise and long startup delays,
  especially with sandboxed `HOME`.

- For PTY-driven TUI testing, use an isolated temp workspace and verify
  side-effects from the shell after each destructive step. See
  `agent_docs/tui-pty-testing.md`.

- Config/theme bootstrap uses `TuiConfigurationBuilder` (MEC-based), not the
  legacy static `ConfigurationManager.Enable(...)`/`Disable(...)` — that API
  is obsolete as of Terminal.Gui 2.4.14 and scheduled for removal. Do not
  reintroduce it. `SkillViewApp.RunAsync` does
  `new TuiConfigurationBuilder(SkillViewAppName).ApplyToStaticFacades()`
  before `WingetTuiTheme.Register(...)` — `ApplyToStaticFacades()` still
  pushes onto the same static `SchemeManager`/`ThemeManager` facades
  `WingetTuiTheme.Register` targets, so that call's ordering/behavior is
  unchanged.

- Terminal.Gui's modern lifecycle is now the right default for SkillView:
  use `Application.Create().Init()` to create the app instance and
  `IApplication.Dispose()` / `using` for teardown. Do not add new uses of the
  legacy static `Application.Init()` / `Application.Shutdown()` path.

- Modal operation ownership must be published before work can run. Use an
  explicit pending publication barrier; `Task.Yield` is insufficient when a
  synchronization context posts inline. Set progress UI before starting work
  so immediate completion cannot be overwritten by a late busy assignment.

- Tie async TUI work to the lifetime of the owning app/dialog with a
  `CancellationToken`, and only update UI through `app.Invoke()` while that
  lifetime is still active. Do not fall back to direct UI mutation after
  teardown.

- Never call `CancellationTokenSource.Cancel()` while holding an ownership
  lock. Cancellation callbacks are arbitrary user code and can synchronously
  re-enter or wait for lease release; publish the ownership transition under
  the lock, cancel outside it, and defer source disposal until cancellation
  finishes. Callbacks can also throw: application-owned cancellation and
  deadline sources must use `SkillView.Threading.CancellationSource`, which
  contains aggregate callback failures from manual, parent, and timer-driven
  cancellation and keeps disposal from racing an active callback. Disposal
  must reject new cancellation admission even while an earlier callback is
  still draining, and construction must release partial parent/timer resources
  if it fails. Do not reintroduce raw linked sources or `CancelAfter` for owned
  work. Every production owner must supply a callback reporter; log the full
  flattened aggregate through `CancellationCallbackReporter` rather than only
  its generic message. Deadline timers must not capture ambient
  `ExecutionContext`.
  Disposing a `CancellationSource` closes cancellation admission immediately,
  so a later `TryCancel()` returns false and intentionally leaves an uncanceled
  stable token uncanceled. Deadline timers must stay disabled until every
  callback-visible field is assigned; construction failures must close
  admission and use the normal active-callback deferral protocol. Capture the
  token value in the lease instead of dereferencing the source after ownership
  can end. Keep the outer cancel-in-progress state in
  `LatestRequestGate` and `CancellationOperationSlot`: it preserves
  cancel-wins-over-lease-dispose ordering in addition to the source's internal
  disposal safety.

- Route application-owned fire-and-forget work through `BackgroundTaskTracker`
  (`SkillViewApp.RunOwnedTask` / `RunBackground`). Keep Discover/Doctor work
  tied to their activation lifetimes and put generation/ownership checks inside
  queued UI callbacks. When a callback still depends on a request lease, await
  `SkillViewApp.InvokeAsync` so the lease cannot be disposed before the UI loop
  executes it. Shared spinner updates must retain an operation owner; ending a
  preview must not clear or hide a still-running search. Shutdown must stop task
  admission, cancel lifetimes, and drain owned work before disposing
  Terminal.Gui or logging resources.

- A submitted Discover search clears the previous results and install targets.
  Keep failure/timeout/cancellation context in the detail pane after transient
  feedback expires. Deactivation clears only Discover-owned feedback and busy
  owners; retain workflow notifications and progress. Status timers must reject
  callbacks superseded by a newer status or workspace transition.

- Keep advanced-remove policy evaluation off the Terminal.Gui thread. Cache
  per-target evaluations only for wizard display and always validate again in
  the owned background operation immediately before execution; if blocking or
  warning content changed, return to Review instead of deleting. Agent-link
  removal is available only through `ValidateSymlink` plus `RemoveAsync`; do
  not restore a public path-only unlink entry point or silently trust a
  `gh skill list` parent as a scan root. Explain `--scan-root` when a reported
  agent link lies outside known roots. Batch deduplication is a skip, not a
  failure, and must remain explicit in logs and report accounting.

- Compact-remove routing is also native policy work: build its targets and
  evaluate compact eligibility in an Installed-tab-owned background operation,
  not in the `onRemove` UI callback. Gate repeated remove shortcuts while that
  preflight is active, cancel it when the tab/app lifetime ends, and seed the
  advanced wizard with the preflight result instead of repeating the same
  native inspection. Installed inventory loading and remove preflight own busy
  state independently; derive their shared spinner/footer from both owners so
  either completion cannot hide the other operation's feedback.
  While preflight runs, mirror its status prominently in the Installed detail
  pane as well as the footer, and do not let selection/filter refreshes
  overwrite that owned detail state. When preflight ends, restore the selected
  detail even when an inventory error remains in the footer. Seed the advanced
  wizard from the preflight evaluation without launching another eager scan;
  evaluate alternative removal scopes only after the user selects them.

- Use `Logger.SubscribeWithReplay` whenever a consumer needs retained history
  plus live entries. Do not recreate snapshot-then-subscribe logic. Preserve
  the logger's message/total-character budgets and the file sink's date+size
  rotation and active-file exclusion when changing logging. Observer delivery
  uses synchronous sequence backpressure rather than retaining out-of-order
  entries; observer callbacks must never write to any `Logger`. Rejecting all
  callback-originated logging before ring mutation prevents both same-thread
  recursion and cross-thread, cross-logger lock cycles (A → B and B → A).

- `SkillViewApp` uses feature partials for layout/navigation, discovery/preview,
  feedback, presentation, workspace lifetimes and dispatch. Keep shared field
  initializers and run-loop construction/teardown together in the main file;
  moving a handler must not change initialization, subscription or shutdown order.
  `CliDispatcher` follows the same feature-partial pattern, with routing/deadlines
  and shared JSON output helpers in its main file.

- The main TUI host path now runs through `SkillViewApp.RunAsync(ct)`, and
  `EntryPoint.RunAsync` awaits it directly. Keep external cancellation wired to
  the app lifetime so Terminal.Gui can stop the active runnable via
  `IApplication.RunAsync(..., ct, ...)`.

- A queued UI dispatch canceled by its owning app/view lifetime is expected
  teardown. Consume that owned `OperationCanceledException` before it reaches
  `BackgroundTaskTracker`; unrelated cancellation and faults must still report.

- When an async operation depends on a replaceable/disposable workspace
  `CancellationTokenSource`, capture its `CancellationToken` before the first
  await and use only that value afterward. Never dereference the source from a
  continuation after navigation can cancel and dispose it.

- Discover agent filtering previews at most four skills concurrently, applies
  a 15-second deadline to each metadata preview, and retains the existing
  two-minute whole-search deadline. Keep scheduling bounded across overlapping
  searches through `SearchAgentMetadataLoader`; do not cache timeout, process,
  or other transient preview failures as an empty agent list.

- A modal operation remains owned until its queued UI completion callback has
  run and explicitly releases or closes the modal. `Task.IsCompleted` only
  describes the worker; it is not permission for keyboard shortcuts to cancel,
  close, retry, or escalate against controls whose completion is still queued.
  Declare the modal control before its `ModalOperationTracker` using declaration
  so reverse-order teardown cancels and drains the operation before disposing
  controls.
  Synchronous install dialogs use `ModalOperationTracker`; its release request
  also waits for the worker itself to return before clearing ownership. Use
  `InvokeTerminalIfActive` for the completion that releases or closes a modal,
  so dispatch/callback failure cannot strand ownership; keep nonterminal
  progress on `InvokeIfActive` so a cosmetic failure cannot release a running
  worker.

- Compact install captures the install service's child CWD/environment for
  default-scope preparation, agent detection and submission. Resolve the
  project base through `GhPlacementCatalog` under that context. Preparation
  owns the modal operation slot; a provisional scope cannot be submitted.
  Keep the selected radio and its focused item aligned at initialization,
  and focus the visible primary action when preparation completes unless the
  user moved focus during lookup. Honor preparation cancellation again inside
  queued completion before enabling controls. Sanitize/bound displayed base
  paths without changing the raw child context or placement paths.

- Awaiting dispatch of an entire synchronous modal needs the stronger owned
  dispatch contract: cancellation may reject a callback that is still queued,
  but once that callback starts the background owner must wait until the nested
  modal run returns. Stage background-completed modal launches for the next
  `IApplication.Iteration`; a worker-thread `IApplication.Invoke` callback runs
  inside Terminal.Gui's `TimedEvents` lock, and running the modal there
  deadlocks any modal worker that reports progress through `Invoke`. Do not use
  a token-short-circuiting cosmetic dispatcher for this boundary or shutdown
  can release ownership while the modal is active.

- CLI entrypoints translate Ctrl+C/external cancellation to exit code 130 and
  apply bounded per-command deadlines. Cancellation closes admission before
  startup side effects, synchronous log cleanup, environment probing, or child
  process launch; `SkillViewApp.RunAsync` also reports 130 directly rather than
  relying on `EntryPoint` to repair a success result. Propagate the root token
  through every adapter. Cancellation regressions must observe the side-effect
  boundary they claim: use an admission seam to prove startup resources were
  not created, drive active TUI cancellation from the event loop, and verify
  teardown; `CancelAfter` plus a final exit-code assertion is not sufficient.
  After process-tree termination, retain the bounded parent-exit wait and
  observe both output drains. Never let a broad
  startup/failure catch convert `OperationCanceledException` into an ordinary
  failed result.

- Tests that mutate Terminal.Gui static configuration belong to the serialized
  `TerminalGuiStaticState` collection. Allocation and process stress tests
  belong to the serialized `ResourceStress` collection. A deadlock regression
  test that deliberately blocks one worker must run its competing operation on
  a dedicated `LongRunning` task; shared-pool scheduling delay under the full
  parallel suite is not evidence about the lock ordering under test.

- `SkillViewApp` now keeps the search shell and pane state, while
  `SkillViewWorkflowCoordinator` owns install/update/installed/remove/cleanup/
  doctor orchestration plus the shared inventory capture/rescan flow. Put new
  workflow-level behavior there unless it truly belongs to the search shell.

- Terminal.Gui `2.5.x` supports `Application.Create().Init()`. Keep using
  `TuiConfigurationBuilder` and check the documented lifecycle, `View.Text`,
  and `IAcceptTarget` API changes before a later package upgrade.

- Prefer `KeyBindings` for view-local command remaps like table preview
  shortcuts. Keep the current window/table `KeyDown` routing where the app is
  intentionally centralizing whole-screen actions (search/install/open/logs,
  installed-screen filter/sort/remove, cleanup actions, etc.).

- On Terminal.Gui `2.5.x`, `TableView.CollectionNavigator = null` is the
  supported way to disable type-to-search. Do not restore the custom matcher.

- Sanitize untrusted text before assigning it to preview/detail/log panes.
  `TerminalEscapeSanitizer` is now the shared UI-layer guard for remote preview
  markdown, search metadata, installed-skill detail markdown, cleanup/remove
  summaries, and rendered log text.

- Terminal.Gui `2.5.x` enables bracketed-paste mode. SkillView does not need
  custom handling for it: editable `TextField` inputs accept terminal-native
  paste through Terminal.Gui's default `Command.Paste` pipeline, while read-only
  panes ignore paste events. `TerminalEscapeSanitizer` still applies to rendered
  remote content and is separate from Terminal.Gui's pasted-input sanitization.

- Keep local inventory I/O off the Terminal.Gui thread. Scanner, cleanup
  classification, root resolution, and package-lock enrichment must accept and
  check cancellation between roots/entries. Read at most the bounded
  front-matter prefix from `SKILL.md` and reject `.skill-lock.json` files over
  the configured byte limit; do not reintroduce whole-file reads for either.

- File-log aggregate retention is enforced as the active part grows, not only
  when a writer opens or rotates. Maintain incremental byte accounting and a
  bounded retry threshold for undeletable files so the fix does not enumerate
  the log directory on every line.

- Keep the main shell's contextual header, inline busy indicators, quit routing,
  cancellation ownership, and memory bounds aligned with
  `agent_docs/ui-lifecycle-and-resource-bounds.md`. In particular, `Ctrl+Q`
  must quit from text fields; the ANSI driver can report bare Esc followed by
  Ctrl+Q as Ctrl+Alt+Q, which must also quit. Keep the opt-in PTY quit regression.
  Subprocess capture and caches must stay bounded,
  and superseded preview/inventory work must be canceled.

- Display inventory captures share a flight only within the current catalog
  generation. Refresh detaches and cancels obsolete work outside the ownership
  lock. Each flight has an owned cancellation lease; its worker links that token
  with the app lifetime. Keep sources until work and cancellation callbacks settle. The app still
  drains these workers; stale completions cannot clear the new flight. Scheduling must report refused
  admission so every display waiter settles during shutdown. Partial or
  root-incomplete snapshots are shared with current waiters but not reused;
  retain their ordering evidence so an older complete flight cannot hide them.
  The next tab visit retries. Removal preflight
  resolves the selected installation uniquely in its fresh snapshot and uses
  that fresh target for both target construction and confirmation. Its gh
  catalog captures bypass the cache without invalidating other operations.
- Installed scope cycling filters the shared full snapshot in-process. Summary
  home shortening uses the captured child HOME/USERPROFILE and CWD; exact review
  paths remain unshortened. Cleanup/Updates previews explicitly request technical
  evidence and omit Installed-only shortcuts. Technical link fields expose their
  destinations; non-HTTP(S) upstream/package URLs are plain text.

- Loading indicators transfer browse focus before hiding a table or detail pane
  and restore it before hiding the indicator. Intentional query/filter focus
  remains editable. Gate selection actions while their rows are hidden or a
  conflicting operation is active; keep navigation, help, quit and refresh
  available. Report removal preflight failures in the owned UI completion with
  readable guidance and full exception logging; canceled owners must not publish
  stale failures. Keep focus-gain bookkeeping separate from visibility handoffs.

- Update availability is app-owned transient observation in
  `UpdateAvailabilitySession`, shared by Installed and Issues. Checks are explicit,
  use at most four exact-copy dry-runs, and never prepare candidates or capture
  payloads. Catalog invalidation cancels checks and marks prior observations stale;
  queued publication checks request, catalog generation, app lifetime and exact
  source/owner evidence. Main review opens the saved-update operation for one
  exact subject without an inventory browser or second copy picker.

- Updates operation review requires an exact seeded subject. Keep its inventory
  loader restricted to that subject across refresh and version-choice deferral;
  a failed/missing/ambiguous capture clears the actionable target. The retired
  full inventory browser, marks/All, UI sibling queue and dry-run action must
  not be reintroduced as test helpers. Main-view availability remains separate.

- Installed builds one transient source/copy projection off the UI thread per new
  snapshot. SourceIdentity.Key plus case-insensitive name groups known sources;
  null/conflicting keys stay singleton groups; duplicate IDs are inspect-only.
  Drawing
  and navigation use frozen labels/addresses, with no discovery or classifier I/O.
  Shared exposures count as placements, not extra physical copies. Preserve all
  owner claims and availability states; read-only policy never invents a check.
- Installed uses full-width list/detail below 110 columns and master/detail above
  that width. Enter browses/toggles; visible Review/u hands off the exact ID/path
  to existing fresh admission. Esc returns detail to list before leaving the tab;
  Left/Right expands locally and / focuses Installed search. P/G/sort persist.
  Keep selection and top-row keys through projection changes, and disable actions
  when that selection is missing, hidden, filtered or ambiguous. Refresh leaves
  old rows visibly refreshing with copy actions paused. Bound display to 8,192
  rows including headings and omission indication. C (Shift+c) remains global
  check/cancel for observed copies or a running check; an empty Installed view
  hides Check updates (C) and consumes C without starting work. c remains Cleanup.
  The existing Issues copy chooser is separate.

- With no observed Installed copies, show a full-width empty explanation, separate
  scanned/configured location counts and honest partial coverage, plus Search
  skills (1) through the existing Discover callback. Keep Coverage/Back available
  and exact-copy actions disabled. Filtered no matches and missing/ambiguous
  selections remain distinct; never clear retained filters or choices implicitly.

- Installed's four visible selectors use stable captured keys, never shortened
  display labels. Tab reaches each direct choice popup; Esc cancels it. Keep
  missing choices visibly unavailable with zero matches until explicit clearing.
  Preserve exact selection, expansion, top-row anchor, sort and detail focus;
  local filters/coverage must not capture, classify, hash or launch gh children.
  Coverage reuses the shared inline detail and Back; no inventory browser/modal.
- Issues exposes only its already-built queue's read-only finding addresses,
  associated with the exact capture reference and catalog generation. Installed
  never loads findings itself. Partial evidence can prove a finding, never safe
  absence; mismatched/stale/not-loaded evidence is unknown. Subjectless findings
  remain Issues-only. Installed captures the evidence reference and catalog
  generation once per view refresh and reuses that pair for every row, choice,
  count and status in that refresh. No new event bus/cache/classifier owns this
  handoff. Restore selector focus only while its view lifetime is still active.

- Compact named-install success stays in the same dialog as an operation result:
  Install completed, View installed skill, Back to Discover. Back/Esc/close,
  including during pending navigation, preserve success and reject late capture
  publication. Completed results cannot submit another install. The coordinator
  captures fresh inventory once, bypassing both display and gh caches, and builds
  the projection off-thread. Queued publication checks modal request ownership,
  app/Discover lifetime, catalog generation and captured context. Seed Installed
  without an activation load; publish the same snapshot through the existing
  CaptureInventoryAsync/DisplayInventoryCache path so subsequent display/Issues
  requests share that capture reference at the catalog generation. Preserve
  newer-snapshot ordering; the reuse TTL is not freshness or mutation permission.
  Return to Discover without rerunning search.
  Match the explicit source against the full snapshot and admitted placement,
  covered targets and stable native observations before applying filters. One
  verified physical copy opens exact detail when every agent has one folder;
  multiple copies select their source group without copy action focus. When an
  agent has alternative folders, each requested agent needs at least one verified
  copy in its own folder set; a single-folder agent still needs that folder.
  Missing agent locations refuse with “Not all requested locations were verified.”
  Require positive scan/gh coverage for displayed copies, without requiring a
  copy or coverage in empty alternatives. Preserve complete/nonpartial inventory,
  ownership, source/subpath, unique group and stable native identity checks for
  all displayed children. Alternatives always select/expand the group, even for
  one copy, with no copy detail/action focus and the notice “Showing verified
  copies of this skill; choose a copy to review it.” Never attribute a copy to the
  just-completed write. Unknown/conflicted/partial/omitted evidence
  stays in the completed result with a specific explanation, without peer fallback.
  Preserve all Installed restrictions if every verified copy matches; otherwise
  clear them together using ordinary ClearFilters and show “Adjusted Installed
  filters to show this result.” Alternative-folder browsing preserves that notice
  and appends its choose-copy notice when filters were adjusted. Preserve sort and
  Discover state. If seeded reveal is rejected, use one ordinary owned Installed
  load/focus; successful reveal never reloads.

- Installed descriptions retain at most 2,048 UTF-16 code units, backing off a
  split surrogate pair. Sanitize and normalize the original bounded source,
  then wrap each raw line with Terminal.Gui's column-aware WordWrapText at the
  current positive Markdown viewport width, reserving the two copy-glyph columns
  (the viewport already excludes its scrollbar). Reflow never wraps generated
  Markdown. Compute the inert no-info backtick fence after final wrapping; its
  delimiter must exceed every resulting run. A trusted [truncated] paragraph
  stays outside the fence and copy contents. Copy may include display line breaks.
  Reflow preserves exact selection/focus and clamps scroll after layout.
- Ordinary placement literals sanitize controls before one FormatCodeSpan pass;
  Markdown wraps styled inline values. Group exact retained path, effective scope
  and captured Context without I/O or inferred roots. Counts use AgentMembership
  structural Distinct, including hidden evidence fields; identical normalized
  agent/profile labels compress with a trusted multiplier outside the literal.
  Show a deterministic prefix of at most 128 whole memberships within 32,768
  encoded characters including headers/notice (reserve 256 for the latter).
  Recompute multipliers from included members and report the exact omitted count;
  keep full headline/footer totals. Order path ordinally, scope ascending, known
  context before null, normalized agent/profile ordinally, then every original
  membership field in declaration order (ordinal/null-first strings, ascending
  enums and false before true). Raw evidence, authority and filters are unchanged.

- Repository subset listing owns a synchronous modal and its background work.
  Failure/empty results retain bounded literal detail with explicit Retry/Cancel;
  Cancel is the default. Never widen a failed or canceled subset request to
  install-all. Retry and queued completion recheck Discover lifetime and captured
  child context before handing the successful listing to the existing picker.
  Compact install failures expose Details with bounded, sanitized plain text for
  the full refusal and retained stdout/stderr. Keep the original result intact;
  output cannot create Markdown, active links or terminal controls.

- Main-shell History is visible and reachable with F4 even from text inputs.
  Its startup hints begin after the shell's first DrawComplete, through app-owned
  background work and lifetime-checked dispatch, independently of gh/inventory.
  Opening History invalidates queued startup hints. Modal refresh and explicit
  Inspect own LatestRequestGate leases through awaited dispatch; selection,
  refresh, leaving and shutdown cancel stale requests and clear old inspections.
  Refresh retains the selected operation ID when it is still present. History
  work remains owned by the app task tracker after a canceled modal closes.

- B9 typed `CapturedFinding` observations distinguish exact captured copy sets
  from exact contextual gh records, with explicit navigation allowlists. Issues
  and Installed's issue facet share this capture/generation through the existing
  evidence handoff. Remote Updates remain the explicit availability session's
  observations. Recorded revision/method differences never synthesize upgrades.
  Issues uses full-width list/detail below 110 columns, preserving selection on
  Back/refresh. Keep both requests use the selected-view lease; canceled or stale
  queued completions cannot publish notices, reopen reviews or steal focus.

- History retirement exposes only actions admitted by its current inspection.
  Clear approval on selection/Refresh/Inspect/confirmation/attempt, guard ID and
  request ownership through dispatch, and default each action's confirmation to
  Cancel. Copy active Updates review/Undo IDs on the UI thread; reject pending
  Updates publication. Keep mutation work app-owned through cancellation and
  history/badge reconciliation. See [retirement](maintenance-operation-retirement.md).

- Install dialogs record attempt receipts before cancelable UI delivery. Their
  ModalOperationTracker owns and drains the execution/reconciliation worker
  task, including forced modal close/shutdown; evaluate returned result fields
  only after that worker settles. Child exit and both pipe drains remain bounded.
  If termination is refused/unconfirmed, preserve positive start and cancellation
  or transport failure with ChildDrained=false; invalidate facts without inventory
  capture, return WriterUnverified/manual-refresh-after-stop guidance, and do not
  start a reaper or wait indefinitely during teardown. For verified child exit,
  reconciliation uses its own bounded deadline, never the canceled review token,
  and does not await UI dispatch. Publish one uncached inventory snapshot per
  submitted possible-write batch with context and catalog generation guards. Pure preflight refusal or opening
  and canceling a review must not invalidate/capture inventory. Generation
  invalidation also retires display, Issues, package and availability facts.
- Show immediate possible-file-change guidance while Installed refreshes, then
  distinguish complete, partial, failed and unverified-writer outcomes with
  literal bounded Details. Child failure/cancellation is not atomic rollback.
  Retain completed/failed/not-attempted subset selectors and never present a
  mixed batch as all installed. Compact success handoff reuses the reconciled
  snapshot with its original context/generation and exact-source checks; it
  cannot recapture or name-match its way around unavailable evidence. Failure
  does not navigate or resubmit Discover. Explicit Refresh remains a user action;
  do not call it to publish reconciliation because it invalidates again and
  repeats the active search.

- Install placement preview owns one background worker and one replaceable pending
  request. Edits cancel the current request; 15-second deadlines and modal disposal
  bound/drain the work. Queued UI callbacks recheck revision, lifetime and captured
  context. Placement/conflict details are literal, scrollable, bounded guidance;
  no placement I/O, hashing or classification belongs in drawing. Keep conflict
  diagnostics ahead of potentially long destination lists.
- Install agent selectors keep detected config/root observations first, expose the
  full catalog through visible More agents, and keep checked agents in the collapsed
  set. Expanding/collapsing never changes selections or Universal fallback. The
  scrollable parent must be focusable so Tab can reach More and the boxes. Detection
  runs during owned preparation under captured child HOME/overrides, never in draw
  and never by launching clients. Advanced's scrollable form keeps fixed result,
  placement-review and cancel actions visible at 80x24.
- Advanced unchanged named Discover sources may use the same strict G1 verifier as
  compact installs. Picker or uncertain placement returns browse only observed
  repository sources from the G2 reconciled snapshot, with no initial group/copy
  selected. A receipt without a current snapshot cannot navigate. Failure, partial
  attempts and canceled owners never navigate automatically or resubmit. Returning
  preserves Discover rows, query text, submitted filters/page, selection and scroll.
- Discover installed markers are a frozen projection of captured display inventory,
  keyed by normalized repository plus actual source subpath. They retain unknown
  source/coverage states and retire immediately at catalog invalidation. They grant
  no mutation admission, skip no preflight and trigger no extra row-level I/O.

- Installed repository browse is an explicit persistent display filter. Its
  Repository indicator derives from the active restriction and survives seeded
  loads, reloads, explicit refresh, selections and transient status changes,
  including a filtered-empty inventory. Clear removes the restriction and indicator
  with the ordinary filters. A valid exact G1 handoff clears a prior repository
  restriction before revealing its verified subject; rejected handoffs leave it
  intact. No inventory or mutation authority comes from this indicator.
- Advanced navigation may reuse Discover's source-path anchor only when both the
  skill and requested version match their initial observed values. A changed
  version gets the existing safe notice, with no new source resolution; changing
  back only restores anchor eligibility under the existing current context,
  generation and exact G1 verification checks.

- The current explicit preview pair is Terminal.Gui `2.5.1-develop.183` and Editor
  `2.5.8-develop.6`; see the November dependency review. The package supplies
  Markdown partial-redraw and output/timer fixes, but those do not replace
  app-owned cancellation, generation/session checks, started-modal completion or
  drain-before-dispose. New framework `InvokeAsync` adoption is a separate
  lifetime change requiring focused parity tests. The current refresh retains
  the existing dispatch/staging helpers. Editor's typed and polymorphic Text
  setters must keep its document/raw text coherent and read-only input inert.

- F5 Locations is a main-shell shortcut from text inputs. Its synchronous dialog
  owns load, directory validation and preference writes through
  ModalOperationTracker; keep all I/O off the event thread, publish completion
  only while the modal/app token is active, and cancel linked work before
  disposing its token registrations. A saved location change advances catalog
  generation before refreshing the current captured discovery context.

  Locations recovery uses the same modal ownership/linked cancellation and queued
  completion checks as directory validation and Save. Never perform review,
  backup, native topology or filesystem work from draw/filter handlers. Enter in
  Directory/Profile adds or is consumed; Enter in list/read-only Selected cannot
  bubble to default Cancel. Focused buttons retain Enter. Recovery is Cancel by
  default, displays full bounded literal errors and stored-versus-session choices,
  and applies only the reviewed native generation. A failed apply requires a fresh
  review; leaving either modal cancels work and rejects late publication.

- Copies browsing lives in the main Installed/Issues views. Family headers only
  expand; exact children own actions. InlineCopyRequest freezes snapshot reference,
  catalog generation, context, exact subject/choice objects, request epoch and
  return state independently of typed finding projections. InlineCopiesView owns
  explicit comparison leases through awaited UI dispatch; selection, Back,
  refresh, context/catalog invalidation and departure retire requests/facts.
  A reference is explicitly chosen and comparison-only. Held capture root IDs
  distinguish aliases independently of generations; internal witnesses never
  enter saved hashes or public comparison JSON. Complete local capture, raw
  equality, root modes, source/revision domains and bounded inventory/consumer
  coverage stay separate. Full ACL/xattr/ADS equivalence remains unknown.
- Cleanup shortcut filters Issues. SelectedCleanupHandoff retains exact fresh
  issue/kind/path/subject matching and exclusive dispatch. CleanupScreen is now
  a focused selected-item operation review; its owned native preflight identity
  must match immediate execution validation. Its executor retains specialist
  batch validation/accounting compatibility, with no general browsing shell.
  Reconciliation runs independently of canceled delivery. GuidedRepairSequence
  stores frozen per-item intents/outcomes only, never prepared sibling candidates;
  explicit Review next waits for prior executor/reconciliation settlement.
  Updates callbacks bind the dispatched exact ID/path, accepted load generation,
  operation instance and saved plan ID. An unrelated review cannot start/settle
  that item; replacing an unstarted review records a skip, while matching work
  retains its own result through reconciliation even after the view is replaced.
- Standalone Move aside is a thin QuarantineStore Preview/Apply/Undo adapter.
  Comparison is never admission. Keep both explicit move approvals and Preview
  fingerprint, exact fresh inventory/native intent, all existing refusals and
  separate Undo preview/approval with no invented --expect contract. Results
  distinguish native movement from bounded post-action inventory verification.
  A failed reconciliation capture retains the native result and reports bounded
  literal failure detail plus Refresh Installed guidance; it cannot imply that
  an already reported move/restore did not happen.
  Reconcile after possible execution independently of canceled modal delivery;
  restart guidance uses quarantine-history/undo, never replacement-only F4 History.
- More-actions and reference popups anchor to their actual control for keyboard
  and mouse acceptance, use a bounded screen rectangle and opaque framed content.

- The B9/Inline UI adapter retains typed finding keys and parent-qualified exact children. ExactEntry admits only verified expanded membership, non-finding singleton rows, or the strict existing typed singleton leaf shape with exact ordinal IDs/path and consistent single ReviewCleanup/ReviewUpdate navigation. Registry, family, intent and coverage observations supply no execution subject. Finding comparison revalidates Id/Difference and all captured members under the owned selection lease; Allowed membership restricts browsing only. Guided requests own a separate selection gate; s is Skip and k/K remain preferences. Source/retention approvals and specialist fresh admission remain independent.
