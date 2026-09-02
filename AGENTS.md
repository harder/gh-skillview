# AGENTS.md

## Repo

`gh-skillview` is a .NET 10 / Terminal.Gui v2 terminal UI and CLI for browsing,
installing, updating, and cleaning up `gh skill` skills.

## Why

This repo exists to make `gh skill` easier to inspect and safer to operate from
the terminal, with both a full-screen TUI and scriptable CLI commands.

## What

- `src/SkillView.Core/` — shared bootstrapping, CLI dispatch, `gh` adapters,
  inventory, logging, and all TUI screens.
- `src/SkillView.App/` — standalone `skillview` entrypoint.
- `src/SkillView.GhExtension/` — `gh skillview` extension entrypoint.
- `tests/SkillView.Tests/` — xUnit coverage for core services, CLI JSON output,
  and TUI helpers.
- `tests/SkillView.IntegrationTests/` — in-process Terminal.Gui integration
  smoke tests using the ANSI driver and one event-loop tick.

## How

- Build/style verification: `dotnet build`
- Full tests: `dotnet test --no-build`
- Run product publish commands sequentially. The App and gh-extension publishes
  share `SkillView.Core` intermediate/output files, so parallel publishes can
  race while writing `SkillView.Core.deps.json` and cause false build failures.
- Integration tests only: `dotnet test --project tests/SkillView.IntegrationTests/SkillView.IntegrationTests.csproj` (a bare project path no longer works under the .NET 10 SDK's `dotnet test` runner)
- Launch TUI: `src/SkillView.App/bin/Debug/net10.0/osx-arm64/skillview`
- For CLI global flags such as `--scan-root`, pass them **before** the
  subcommand: `skillview --scan-root /tmp/root list --json`

## Critical agent notes

- Keep this file and `agent_docs/` up to date as new durable agent-facing
  workflow or testing lessons are discovered. Put short repo-wide rules here;
  put detailed procedures in a focused file under `agent_docs/`.
- Prefer the built `skillview` host binary over `dotnet run` for PTY-driven TUI
  automation. `dotnet run` can add first-run noise and long startup delays,
  especially with sandboxed `HOME`.
- For PTY-driven TUI testing, use an isolated temp workspace and verify
  side-effects from the shell after each destructive step. See
  `agent_docs/tui-pty-testing.md`.
- SkillView requires `gh` 2.99.0 or newer (`GhBinaryLocator.MinimumVersion`);
  that version guarantees the full `gh skill` surface SkillView relies on,
  including the current Codex and Pi user-scope location rules, so
  there is no per-flag capability probe — only a single `gh skill --help`
  smoke check.
- `InstallAgentCatalog` tracks the full `gh skill install --help` `--agent`
  list as of `gh` 2.99.0 (still 48 entries; gh 2.97.0 replaced `windsurf`
  with `devin` and added `grok`, cli/cli#13987 and cli/cli#13864; gh 2.99.0
  makes Pi's `PI_CODING_AGENT_DIR` override authoritative).
  `HomeRelativePath` is best-effort and
  nullable — only set when the on-disk config-dir convention was
  independently verified; a null path just means that agent is never
  pre-checked or shown in the Doctor "detected agents" table (cosmetic only,
  never gates the actual install call). Re-diff against
  `gh skill install --help` and update the catalog when bumping the `gh`
  minimum again. The agent checkboxes in `InstallScreen`, `InstallConfirmModal`,
  and `RepoSkillPickerModal` render via the shared `AgentCheckboxGrid` helper
  inside a scrollable `View` (`ViewportSettingsFlags.HasVerticalScrollBar`) —
  don't revert to a fixed-row/fixed-height layout, the catalog is too long to
  fit on screen unscrolled.
- Config/theme bootstrap uses `TuiConfigurationBuilder` (MEC-based), not the
  legacy static `ConfigurationManager.Enable(...)`/`Disable(...)` — that API
  is obsolete as of Terminal.Gui 2.4.14 and scheduled for removal. Do not
  reintroduce it. `SkillViewApp.RunAsync` does
  `new TuiConfigurationBuilder(SkillViewAppName).ApplyToStaticFacades()`
  before `WingetTuiTheme.Register(...)` — `ApplyToStaticFacades()` still
  pushes onto the same static `SchemeManager`/`ThemeManager` facades
  `WingetTuiTheme.Register` targets, so that call's ordering/behavior is
  unchanged.
- Install flows default to `--agent universal` (not blank) when the user
  hasn't explicitly checked any agent checkboxes — `InstallConfirmModal.
  DefaultAgents`, shared by `InstallScreen` and `RepoSkillPickerModal` (via
  `BuildOptionsFromSelection`). Verified against live `gh` 2.96.0: a blank
  `--agent` at user scope installs to gh's default (`~/.copilot/skills`),
  while `--agent universal --scope user` installs to the shared, agent-
  agnostic `~/.agents/skills`. Skipped when a custom `--dir` path is set
  (`--dir` overrides `--agent` entirely). `ScanRootResolver.UserSeeds` has a
  matching `.agents/skills` entry so the inventory scan actually discovers
  skills installed there — keep both in sync if this default ever changes.
- Current package compatibility: SkillView is pinned to Terminal.Gui `2.4.17`
  and Terminal.Gui.Editor `2.5.7`, the latest stable releases of each as of
  2026-09. Test projects use `Microsoft.NET.Test.Sdk` `18.9.0`, `xunit.v3`
  `4.0.0`, and `xunit.runner.visualstudio` `4.0.0`. If tests fail to compile on
  missing `TestContext`, rerun `dotnet restore` so stale xUnit 2.x assets are
  replaced. `tests/SkillView.Tests/Build/PackageReferenceTests.cs` and
  `CliDispatcherHelpTests.VersionFlag_IncludesTerminalGuiVersion` hardcode the
  exact Terminal.Gui version string — update both alongside any bump.
- xunit was bumped 3.x → 4.0.0 (2026-08); the project didn't use any of the
  now-obsolete parallelization APIs (`ParallelizeTestCollections`,
  `[CollectionBehavior]`) so no test-code migration was needed. That bump also
  triggered a required `global.json` change: `.NET 10 SDK` removed the legacy
  VSTest-bridge `dotnet test` path entirely, so `global.json` now sets
  `"test": {"runner": "Microsoft.Testing.Platform"}` to opt into the new MTP
  `dotnet test` mode. See `agent_docs/running-tests.md` for the resulting
  command-syntax changes: `--project` is now required (bare/positional project
  paths no longer work), and filtered runs need xunit's MTP-native
  `--filter-namespace`/`--filter-class`/`--filter-trait` flags passed straight
  to `dotnet test`. For one exact method, use xunit's query filter, for example
  `--filter-query "/*/Namespace/Class/Method"`; with the current xunit 4.0.0
  integration, `--filter-method` can silently select zero tests even when the
  discovered fully qualified name matches. xunit's older
  single-dash console-runner flags (`-namespace`, `-trait`, ...) and the
  platform's own `--treenode-filter` are silently rejected by `dotnet test`'s
  MTP handshake here and report "Zero tests ran" instead of erroring loudly.
- `src/SkillView.Core/SkillView.Core.csproj` owns the default
  `TerminalGuiVersion` property. Keep the `PackageReference` on
  `Version="$(TerminalGuiVersion)"` so CI can override it via MSBuild without
  editing source.
- Terminal.Gui's modern lifecycle is now the right default for SkillView:
  use `Application.Create().Init()` to create the app instance and
  `IApplication.Dispose()` / `using` for teardown. Do not add new uses of the
  legacy static `Application.Init()` / `Application.Shutdown()` path.
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
- Removal traversal must never use recursive filesystem enumeration. Treat
  every `FileAttributes.ReparsePoint` child (Unix symlink, Windows junction,
  mount point, or broken link) as a leaf, revalidate containment immediately
  before deletion, and keep cancellation/depth bounds explicit. Keep traversal
  lazy with per-depth enumerator frames so cancellation runs between entries
  and retained state stays O(depth). TUI removal must use `RemoveAsync` /
  `RemoveManyAsync`, whose progress is throttled to 10 updates/second; keep the
  modal alive until cancellation quiesces and rescan when cancellation made
  any partial filesystem change. The advanced remove confirmation is bound to
  the exact captured directory/link identities and execution mode the user
  reviewed; finish-time validation must return to Review if any of those
  values changed, even when paths and policy messages are identical. Runtime
  reports retain at most 128 individual
  errors plus an omission summary while preserving the exact error count. Do
  not overwrite a mid-target cancellation snapshot with completed-target-only
  totals: the batch progress adapter owns the latest aggregate state and keeps
  processed-target and successfully-deleted-target counts distinct. Retryable
  remove dialogs accumulate filesystem mutation totals across attempts and
  compact-to-wizard escalation so later failures cannot hide a required rescan.
  Every async removal entry point must publish a terminal canceled update even
  when its token was already canceled; synthetic cancellation reports retain
  the exact observed runtime-error count and mark cancellation explicitly.
  All non-canceled return paths, including validation refusals, must publish a
  terminal completed progress update with exact processed/deleted/error counts.
  CLI JSON must not duplicate validation refusals or unaccepted warnings into
  `runtimeErrors`. Real removals on supported platforms must stay on
  `SecureRemovalBackend`: validation pins the target's native volume/device and
  full file/inode identity, then re-runs containment and all other policy checks
  against that captured canonical deletion address. Root containment is one
  native authority boundary: open and pin the matched scan root first, open the
  selected directory or link parent relative to that held root, and derive both
  canonical addresses before verifying the root generation. Never canonicalize
  target and root through separate opens. Canonical addresses must
  come from the opened object (`GetFinalPathNameByHandleW` on Windows,
  `fgetattrlist(ATTR_CMN_FULLPATH)` on macOS, and `/proc/self/fd` on Linux),
  never only from lexical normalization; canonicalize scan roots through the
  same handle-based path before comparing them. Linux does not escape its
  ` (deleted)` `/proc/self/fd` annotation, so a live name can legitimately end
  with that text; disambiguate it by comparing the named path's full native
  identity and generation with the still-open descriptor, never by suffix
  rejection alone. Non-destructive Unix scan-root canonicalization must open
  the `realpath` result directly so filesystem root `/` remains valid;
  destructive helpers must continue refusing filesystem-root targets. Windows
  uses `FileIdInfo`
  and `FileIdExtdDirectoryInfo` so ReFS's full 128-bit IDs are compared, and
  pairs them with `FILE_BASIC_INFO.CreationTime`/`ChangeTime` for selected
  directories plus validated parents and links so immediate ID reuse is
  refused. It enumerates opened directory handles, opens every enumerated child and link
  relative to its held parent with `NtOpenFile`/`OBJECT_ATTRIBUTES.RootDirectory`,
  and deletes opened objects with
  `FileDispositionInfoEx` (falling back for `ERROR_NOT_SUPPORTED` as well as
  invalid-function/parameter). Treat that legacy fallback as a separate
  destructive boundary and recheck cancellation immediately before it;
  `FILE_DISPOSITION_INFO.DeleteFile` is a one-byte Win32 `BOOLEAN`, not the
  four-byte `BOOL` used by the API return value. Unix walks canonicalized paths
  through `openat`, enumerates opened directory descriptors, compares
  device/inode identities, uses Linux `openat2(RESOLVE_NO_XDEV)` to reject bind
  and filesystem mounts, refuses device changes on macOS, and deletes with
  `unlinkat`. Apply that mount boundary while validating the selected directory
  and every link-parent component relative to the held scan root, not only
  while descending during deletion. Probe the exact Linux `openat2` flags
  before enabling the backend
  relative to an opened `/` descriptor, never process CWD, so an old kernel or
  seccomp denial fails before validation or mutation without a deleted working
  directory producing a false negative. Retry interruptible Unix open/stat/
  enumeration/delete calls on `EINTR` with a bound; never retry `close`, and
  recheck cancellation before every retried `unlinkat` attempt.
  Linux native `stat` parsing must select the verified layout for
  the current process architecture: little-endian x64 reads `st_mode` at byte
  24, little-endian ARM64 at byte 16, and both read `st_ctim` at bytes 104/112;
  unverified architectures or endianness disable secure removal rather than
  guessing. The unsuffixed macOS libc symbols expose the verified 64-bit-inode
  ABI only to native ARM64 processes; fail closed on Intel/Rosetta rather than
  parsing the ARM64 layout. Unix captured identity includes change-time seconds/nanoseconds as
  well as device/inode for selected directories, parents, links, and observed
  entries because filesystems may immediately recycle an inode. A selected
  directory whose generation or contents changed after validation must be
  refused and revalidated, not accepted for compatibility with stale behavior.
  Do not fall back to
  path-recursive deletion on Windows, macOS, or Linux. Keep cancellation
  checks immediately before every native destructive call and dispose handles
  even when identity inspection, callbacks, or those checks throw. There is no
  unsupported-platform path deletion fallback: real removal is an environment
  refusal when the secure backend is unavailable. `remove` without `--yes`
  remains a non-mutating managed preview and cannot produce an execution
  identity. Do not
  overstate the Unix guarantee: POSIX has no general
  unlink-by-descriptor operation, so the final `fstatat` → `unlinkat` name
  interval remains non-atomic for every entry, including directories and the
  selected root, against a process with the same UID. It cannot redirect
  recursive traversal through an ancestor or replacement directory, but an
  empty replacement at that final name can itself be unlinked.
  Every real directory removal requires a captured `ExecutionIdentity`. Every
  real link-only removal requires an `ExecutionLinkIdentity` that pins the
  canonical parent identity, final name, and native link identity; reverify the
  parent and link immediately before deleting the opened/relative entry. Empty-
  directory cleanup must use `RemoveValidator.ValidateEmptyDirectory`, which
  captures identity, canonicalizes containment, refuses scan roots, and sets
  `RequiresEmptyDirectory`. Both native backends must fail without deleting any
  children if such a directory is populated after validation—never downgrade
  that path into ordinary recursive removal. Broken-link cleanup similarly uses
  `RemoveValidator.ValidateBrokenSymlink` and its identity-pinned link-only
  contract; agent unlink actions use `ValidateSymlink` with the inventory roots.
  Cleanup batches must enumerate validations lazily so each candidate is pinned
  immediately before its own removal; deleting one sibling link changes the
  parent generation and intentionally invalidates any earlier sibling capture.
  Deduplicate all selected cleanup path keys before yielding the first
  validation in both TUI and CLI apply flows, then carry that pre-validation
  skip count into successful and canceled result accounting; otherwise the
  first deletion can make a later duplicate look like a failed validation or
  environment refusal, and cancellation can make a known skip look like a
  failure.
  Destructive directory identity capture may resolve ancestor components, but
  must open the final candidate name relative to the canonical parent with
  no-follow and directory-only flags. Only non-destructive canonicalization may
  follow the final component. When reviewing this boundary, inventory every
  observe → reopen → compare → delete step: a child path reconstructed after a
  parent handle was acquired is not an authority address. Require both the
  platform's full native object ID and its generation/change-time signal at
  every validation-to-execution policy boundary; never assume ID reuse is
  Unix-only. Transient traversal children still compare their full IDs and
  types, but are made authoritative by opening relative to the held parent;
  directory-enumeration timestamps are not a stable validation contract.
  Object-local removal policy must be captured through that same held directory:
  inspect `SKILL.md`, `.git`, and empty-directory state relative to the opened
  handle/descriptor, then verify the directory generation did not change during
  inspection. Windows filesystems can either preserve or change `ChangeTime`
  across a rename, so ABA tests must accept only the two safe outcomes: a
  handle-bound snapshot when generation remains stable, or an explicit
  generation-change refusal. Never capture identity and perform those checks later through its
  canonical pathname. Broken-link eligibility likewise belongs to the captured
  parent/name/link observation: determine whether the target resolves relative
  to the held parent, then recheck parent and link identities before approving
  cleanup. Adversarial coverage must combine ancestor rename/replacement with hard links,
  final-component symlink replacement, and immediate native-identifier reuse
  on every supported OS.
  `GetFinalPathNameByHandleW` normalization may strip `\\?\` only from DOS
  drive-letter paths and convert `\\?\UNC\` to ordinary UNC form. Preserve
  other extended namespaces such as `\\?\Volume{GUID}\...`; stripping their
  prefix turns them into unsafe relative paths.
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
- Use `Logger.SubscribeWithReplay` whenever a consumer needs retained history
  plus live entries. Do not recreate snapshot-then-subscribe logic. Preserve
  the logger's message/total-character budgets and the file sink's date+size
  rotation and active-file exclusion when changing logging. Observer delivery
  uses synchronous sequence backpressure rather than retaining out-of-order
  entries; observer callbacks must never write to any `Logger`. Rejecting all
  callback-originated logging before ring mutation prevents both same-thread
  recursion and cross-thread, cross-logger lock cycles (A → B and B → A).
- A successful `gh skill list` process result is cacheable only when its JSON
  parses as the expected inventory schema. Preserve the distinction between a
  valid empty `[]` payload and malformed, truncated, or schema-incompatible
  output, which must remain retryable. The canonical/legacy skill-name field
  is required to be a nonblank JSON string; do not coerce numbers into names.
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
  modal run returns. Do not use a token-short-circuiting cosmetic dispatcher for
  this boundary or shutdown can release ownership while the modal is active.
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
- Use `PathIdentity` for path normalization, keys, equality, and containment.
  Do not substitute a global Windows/macOS ignore-case rule: Windows supports
  case-sensitive directories and macOS supports case-sensitive volumes. A
  missing direct child must probe its containing directory, not the containing
  directory's name in its parent. Preserve case conservatively when that
  directory is missing, empty, or cannot be probed.
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
- Package-manager dark-launch scaffolding lives under `packaging/` and the
  release workflow only generates Homebrew / WinGet artifacts when the repo
  variables (`HOMEBREW_TAP_ENABLED`, `HOMEBREW_TAP_REPO`, `WINGET_ENABLED`) are
  explicitly enabled. It does not push to a tap repo or submit to WinGet yet.
- Terminal.Gui `2.4.17` remains compatible with the modern
  `Application.Create().Init()` lifecycle; the local
  `UnconditionalSuppressMessage` workaround and temporary App-level warning mask
  stay removed after a verification publish proved the App entrypoint no longer
  needs them.
- CI's standalone AOT smoke publish now promotes `IL2026`, `IL3050`, and
  `IL3053` to errors for `SkillView.App`; keep the gh extension's project-level
  suppression local until that host gets its separate re-evaluation.
- Prefer `KeyBindings` for view-local command remaps like table preview
  shortcuts. Keep the current window/table `KeyDown` routing where the app is
  intentionally centralizing whole-screen actions (search/install/open/logs,
  installed-screen filter/sort/remove, cleanup actions, etc.), not because of
  the old `TableView` type-to-search swallowing bug.
- On Terminal.Gui `2.4.17`, `TableView.CollectionNavigator = null` is the
  supported way to disable type-to-search. Treat `#5232` as the fix for the old
  printable-key swallowing behavior and prefer this documented path over the old
  custom matcher workaround.
- Sanitize untrusted text before assigning it to preview/detail/log panes.
  `TerminalEscapeSanitizer` is now the shared UI-layer guard for remote preview
  markdown, search metadata, installed-skill detail markdown, cleanup/remove
  summaries, and rendered log text.
- Terminal.Gui `2.4.17` enables bracketed-paste mode. SkillView does not need
  custom handling for it: editable `TextField` inputs accept terminal-native
  paste through Terminal.Gui's default `Command.Paste` pipeline, while read-only
  panes ignore paste events. `TerminalEscapeSanitizer` still applies to rendered
  remote content and is separate from Terminal.Gui's pasted-input sanitization.
- Keep local inventory I/O off the Terminal.Gui thread. Scanner, cleanup
  classification, root resolution, and package-lock enrichment must accept and
  check cancellation between roots/entries. Read at most the bounded
  front-matter prefix from `SKILL.md` and reject `.skill-lock.json` files over
  the configured byte limit; do not reintroduce whole-file reads for either.
- Keep filesystem inventory aligned with `gh skill`'s configuration-directory
  overrides: Claude uses `CLAUDE_CONFIG_DIR/skills` and Pi uses
  `PI_CODING_AGENT_DIR/skills` (gh 2.99+). Scan the default user root alongside
  either override so older/manual skills remain visible, and use the same Pi
  override when pre-checking install-agent boxes and rendering Doctor.
- File-log aggregate retention is enforced as the active part grows, not only
  when a writer opens or rotates. Maintain incremental byte accounting and a
  bounded retry threshold for undeletable files so the fix does not enumerate
  the log directory on every line.
- Keep the main shell's contextual header, inline busy indicators, quit routing,
  cancellation ownership, and memory bounds aligned with
  `agent_docs/ui-lifecycle-and-resource-bounds.md`. In particular, `Ctrl+Q`
  must quit from text fields, subprocess capture and caches must stay bounded,
  and superseded preview/inventory work must be canceled.
- If Copilot-specific, Claude-specific, or other agent-platform guidance turns
  out to matter for this repo, capture the repo-relevant part here so future
  agents do not need to rediscover it from external docs.

## Progressive disclosure

- `agent_docs/running-tests.md` — standard verification commands, UI-focused
  test filters, and opt-in `gh` contract-test workflow details.
- `agent_docs/release-engineering.md` — release workflow, asset naming, AOT RID
  matrix, and attestation conventions.
- `docs/runbooks/release-rollback.md` — rollback steps for live GitHub Releases
  and the current Homebrew / WinGet dark-launch artifacts.
- `agent_docs/tui-pty-testing.md` — sandboxed PTY workflow, synchronization
  strategy, verification scripts, and known pitfalls for terminal UI testing.
- `agent_docs/ui-lifecycle-and-resource-bounds.md` — shell conventions,
  cancellation ownership, and subprocess/cache/log memory limits.
