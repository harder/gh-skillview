# TUI and asynchronous lifetime contracts

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

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

- Terminal.Gui `2.5.0` supports `Application.Create().Init()`. Keep using
  `TuiConfigurationBuilder` and check the documented lifecycle, `View.Text`,
  and `IAcceptTarget` API changes before a later package upgrade.

- Prefer `KeyBindings` for view-local command remaps like table preview
  shortcuts. Keep the current window/table `KeyDown` routing where the app is
  intentionally centralizing whole-screen actions (search/install/open/logs,
  installed-screen filter/sort/remove, cleanup actions, etc.).

- On Terminal.Gui `2.5.0`, `TableView.CollectionNavigator = null` is the
  supported way to disable type-to-search. Do not restore the custom matcher.

- Sanitize untrusted text before assigning it to preview/detail/log panes.
  `TerminalEscapeSanitizer` is now the shared UI-layer guard for remote preview
  markdown, search metadata, installed-skill detail markdown, cleanup/remove
  summaries, and rendered log text.

- Terminal.Gui `2.5.0` enables bracketed-paste mode. SkillView does not need
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
