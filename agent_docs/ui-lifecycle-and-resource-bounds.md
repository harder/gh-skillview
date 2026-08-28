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
  Continue to pass cancellation into subprocess-backed services.
- `ProcessRunner` retains at most 1 MiB of child output per stdout/stderr stream,
  plus a small truncation marker. It drains both streams in fixed-size chunks;
  do not use line-based process events because a newline-free child can make the
  framework retain an unbounded line before SkillView sees it. Increase the
  capture limit only with evidence that a supported command needs more output.
- `SearchAgentMetadataCache` is a thread-safe 512-entry LRU. Do not replace it
  with an unbounded dictionary.
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
  Traversal retains O(depth) enumerator state and no more than 128 detailed
  runtime errors plus one omission summary.
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

## Focused verification

Run `dotnet build`, then `dotnet test --no-build`. Resource-focused coverage
includes large subprocess output, LRU eviction, subscription disposal,
callback-safe cancellation gates, bounded inventory files, iteration failures,
overlapping preview cancellation, superseded inventory scans, asynchronous
removal cancellation/progress/error bounds, inline busy state, and layout
guards at 80×24, 100×30, and 140×42.
