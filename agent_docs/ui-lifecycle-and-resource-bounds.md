# UI lifecycle and resource bounds

Use these rules when changing the main shell, asynchronous tab work, previews,
logging, or subprocess adapters.

## Shell and interaction conventions

- The window border owns the `SkillView — skillview` / `gh skillview` brand.
  Do not add another persistent logo. `ContextBarView` owns the active workspace
  title and its optional context chips.
- Keep search scope beside the relevant search field. Put actions in the bottom
  status strip and keep table abbreviations in a separate legend.
- A `SpinnerView` must be immediately before its status label on the same row.
  The main spinner is owned by `StatusStripView`; tab and modal spinners use the
  same inline layout.
- Configure rendered Markdown through `TuiHelpers.ConfigureMarkdownPane`, which
  hides literal heading prefixes while retaining semantic heading styles.
- `Ctrl+Q` is unconditional and must be handled before printable-key routing.
  Plain `q` is a top-level quit shortcut; `Esc` means leave field, back, or close.
- Layouts below 80×24 show `TerminalSizeGuardView` instead of overlapping panes.

## Lifetime and memory rules

- `LatestRequestGate` owns Discover preview cancellation. A new preview cancels
  the previous request, and every completion checks `Lease.IsCurrent` before
  updating UI. Search cancels previews both when the search starts and when its
  new result set is committed; the second boundary catches previews launched
  from the old table while the search was in flight. Search and preview await
  their queued UI dispatches before disposing the request lease; a fire-and-
  forget `Invoke` must not capture `Lease.IsCurrent`.
- Discover search and preview capture the workspace `CancellationToken` before
  their first await. Navigation exchanges, cancels, and disposes the owning
  source, so no continuation may read `.Token` from that source afterward.
- The shared status spinner uses operation IDs. Ending or canceling one owner
  reveals the newest remaining operation instead of clearing the spinner
  globally. In particular, canceling an old-table preview while search remains
  active must restore the search text and spinner.
- Installed, Changes, and Updates cancel superseded inventory loads. Pass their
  cancellation token through to inventory capture; do not add generation-only
  refreshes that leave obsolete scans running. Use `CancellationTokenSourceSlot`
  so source replacement, cancellation, and lease disposal share one ownership
  boundary. Publish the transition while holding the slot/gate lock, then run
  `Cancel()` callbacks outside that lock and defer source disposal until the
  cancellation call completes. A callback can synchronously wait for lease
  release, so calling it under the ownership lock is a real deadlock.
- Async update/install controls stay disabled while their operation is active.
  Continue to pass cancellation into subprocess-backed services. Synchronous
  install dialogs use `ModalOperationTracker`: capture its stable token, retain
  non-null ownership until the UI commit and worker return are both complete,
  gate progress through `InvokeIfActive`, terminal commits through
  `InvokeTerminalIfActive`, and let disposal cancel and drain before any
  controls are disposed. The terminal form releases ownership if dispatch or
  the callback throws; the progress form must not release a worker that is
  still active.
- CLI and TUI hosts share `EntryPoint` root cancellation. CLI operations must
  propagate it through every adapter and retain a bounded command deadline.
  `ProcessRunner` whole-tree termination is followed by a five-second bounded
  parent-exit wait and observation of both output drains.
- Use `PathIdentity.NormalizeKey`, `PathIdentity.Equals`, and
  `PathIdentity.IsInside` for filesystem identity. Do not introduce platform-
  global case assumptions: macOS volumes and Windows directories can opt into
  the opposite of their common case behavior.
- `ProcessRunner` retains at most 1 MiB of child output per stdout/stderr stream,
  plus a small truncation marker. It drains both streams in fixed-size chunks;
  do not use line-based process events because a newline-free child can make the
  framework retain an unbounded line before SkillView sees it. Increase the
  capture limit only with evidence that a supported command needs more output.
- `SearchAgentMetadataCache` is a thread-safe 512-entry LRU. Do not replace it
  with an unbounded dictionary.
- Discover agent metadata loading has a global four-preview concurrency bound,
  a 15-second deadline per preview, and the search request's two-minute outer
  deadline. A timeout or failed preview stays retryable; only a successful
  preview or a result with definitively absent repository metadata is cached.
  Keep both the per-request worker bound and shared slot bound so superseded
  searches cannot temporarily multiply process concurrency.
- Local inventory capture schedules synchronous filesystem work on the thread
  pool and runs it concurrently with `gh skill list`. Resolver, scanner,
  package-lock reader, and cleanup classification check cancellation between
  roots and entries. `LocalSkillScanner` reads no more than 64 KiB from each
  `SKILL.md`; `SkillLockFileReader` ignores manifests over 1 MiB. Keep actual
  lazy-enumerator `MoveNext` calls inside the I/O exception boundary because
  disconnects, ACL changes, and disappearing directories fail there rather
  than when the enumerable is created.
- Removal uses the same thread-pool boundary: compact remove, the advanced
  wizard, cleanup batches, and agent-link unlinking call the asynchronous
  `RemoveService` APIs. Progress callbacks are throttled to 10 per second and
  immediately handed to `IApplication.Invoke`; they must not use `Progress<T>`
  and add another implicit queue. Esc cancels the active traversal, and each
  removal window cancels and drains its owned task before disposing controls.
  Partial cancellation counts still trigger inventory invalidation/rescan.
  `BatchProgressAdapter` retains the latest aggregate snapshot so the outer
  cancellation boundary cannot replace mid-target file/directory counts with
  completed-target-only totals. Progress tracks processed targets separately
  from successfully deleted targets; cancellation summaries must never treat a
  failed-but-processed target as removed. Each async removal entry point emits
  a final `IsCanceled` update for already-canceled tokens as well as in-flight
  work. Retryable remove dialogs retain cumulative file/directory mutation
  totals across attempts and across compact-to-wizard escalation, while the
  displayed progress remains per-attempt. Synthetic cancellation reports mark
  cancellation explicitly and retain the exact runtime-error count even when
  the individual logged details are unavailable.
  Traversal retains O(depth) enumerator/handle state and no more than 128
  detailed runtime errors plus one omission summary. Validation pins the
  selected target's native filesystem identity and validates the captured
  canonical deletion address. Windows removal compares full 128-bit file IDs,
  enumerates and deletes opened handles; Unix removal walks and enumerates opened directory
  descriptors, compares device/inode identities, rejects Linux bind/filesystem
  mounts with `openat2(RESOLVE_NO_XDEV)`, and refuses macOS device changes. Keep
  actual deletion on `SecureRemovalBackend` for Windows, macOS,
  and Linux. Check cancellation immediately before each native delete and keep
  handle cleanup in `finally` paths. POSIX `unlinkat` is parent-handle-relative
  but still names its final entry, so document rather than conceal its narrow
  final-name race for files, links, directories, and the selected root; the
  held-directory design prevents a replacement ancestor or child directory
  from redirecting recursive traversal, though an empty final-name replacement
  can be removed.
  Real directory execution fails closed without a pinned native identity.
  Empty-directory cleanup is a distinct execution contract: validation pins
  the identity and native traversal refuses immediately if it observes any
  child, so a directory populated after validation is not recursively cleaned.
  Identity-less execution is reserved for validations explicitly marked as
  link-only, and those refuse if the path is no longer a link.
- Logger subscriptions are disposable. Every long-lived subscriber must retain
  and dispose its subscription. Disposal deactivates registrations that were
  already snapshotted and waits for an in-flight callback, so no callback can
  begin after disposal returns. Out-of-order concurrent delivery waits for the
  missing sequence instead of retaining pending log entries. Observer callbacks
  must not write to any logger: the thread-local callback-depth guard rejects
  the write before ring mutation, preventing direct recursion, same-thread
  A → B → A cycles, and concurrent A → B / B → A lock inversion without
  allocating per-entry tracking collections. The visible log pane retains at
  most 512 formatted lines and coalesces burst refreshes without lost wakeups.
- `FileLogSink` tracks aggregate retained bytes after its initial trim and
  re-enforces the disk budget on the first append that crosses it. When the
  active part alone is oversized or an old file is undeletable, retries occur
  after bounded growth rather than once per line.
- Fire-and-forget UI work must consume `OperationCanceledException` only when
  its own app/view lifetime is canceled. Normal shutdown must not become a
  false `CRASH`, while unrelated cancellation and real faults remain visible.
- For synchronous modals that own an async task, non-null task identity is the
  ownership boundary. A completed worker may still have an `IApplication.Invoke`
  completion queued; shortcuts must remain blocked until that callback clears
  the task or closes the modal. Cancel only when the owned task is still
  running, then drain it before controls are disposed.

## Focused verification

Run `dotnet build`, then `dotnet test --no-build`. Tests that mutate
Terminal.Gui static configuration belong to the nonparallel
`TerminalGuiStaticState` collection; allocation/process stress tests belong to
the nonparallel `ResourceStress` collection. Resource-focused coverage
includes large subprocess output, LRU eviction, subscription disposal,
callback-safe cancellation gates, bounded inventory files, iteration failures,
overlapping preview cancellation, superseded inventory scans, asynchronous
removal cancellation/progress/error bounds, bounded and timed metadata
previews, streaming CLI JSON, install-modal ownership, inline busy state, and layout
guards at 80×24, 100×30, and 140×42.
