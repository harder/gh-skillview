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
  still active. Because C# using declarations dispose in reverse order, declare
  the modal control first and its `ModalOperationTracker` second.
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
  wizard, cleanup batches, and agent-link unlinking call `RemoveAsync`/
  `RemoveManyAsync` with a root-validated `RemoveValidation`. The advanced
  wizard also evaluates native filesystem policy off the UI thread, caches
  results only for display, and refreshes the selected target immediately
  before execution. If refreshed errors or warnings differ, it returns to
  Review. Progress callbacks are throttled to 10 per second and
  immediately handed to `IApplication.Invoke`; they must not use `Progress<T>`
  and add another implicit queue. Esc cancels the active traversal, and each
  removal window cancels and drains its owned task before disposing controls.
  The Installed tab owns compact-remove preflight as an asynchronous operation:
  both target construction and native compact-eligibility evaluation run off
  the UI thread, repeated remove shortcuts are ignored until it completes, and
  tab/app cancellation prevents a delayed dialog from opening. The resulting
  primary evaluation is passed into the wizard when compact mode is unsuitable,
  avoiding a duplicate native inspection while retaining finish-time
  revalidation before deletion.
  Dispatching the compact/wizard decision uses a start-aware owned callback:
  cancellation rejects it while it is queued, but cannot complete its owner
  after the callback has entered the synchronous nested modal loop. This keeps
  app shutdown from draining the background task before the modal returns.
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
  canonical deletion address. Containment capture opens the matched scan root
  first and then opens the target directory or link parent relative to that
  held object; target and root canonicalization must never be separate,
  independently raceable opens. The canonical address and canonical scan roots
  come from opened handles, not lexical normalization: Windows uses
  `GetFinalPathNameByHandleW`, macOS requests `ATTR_CMN_FULLPATH` with
  `fgetattrlist`, and Linux reads `/proc/self/fd`. Because Linux's unescaped
  ` (deleted)` annotation is also legal filename text, a returned path ending
  with it is accepted only when that name's native identity and generation
  match the still-open descriptor; suffix text alone is not evidence that the
  entry was unlinked. Non-destructive Unix canonicalization opens the resolved
  directory directly so `/` is a valid scan root, while destructive helpers
  continue refusing a filesystem-root target. Windows removal compares full
  128-bit file IDs plus `FILE_BASIC_INFO.CreationTime`/`ChangeTime`, opens
  enumerated children and links relative to the held parent handle with
  `NtOpenFile` and
  `OBJECT_ATTRIBUTES.RootDirectory`, and deletes opened handles; Unix removal
  walks and enumerates opened directory
  descriptors, compares device/inode identities, rejects Linux bind/filesystem
  mounts with `openat2(RESOLVE_NO_XDEV)`, and refuses macOS device changes. The
  same no-cross-device boundary applies while opening a selected directory or
  link parent relative to the held scan root during validation; deletion-time
  traversal checks alone are too late because the captured target could already
  belong to an external mount.
  Linux backend probes the exact `openat2` contract at startup relative to an
  opened filesystem-root descriptor, not process CWD, and remains disabled
  when the kernel or sandbox refuses it, before any mutation can begin. Native
  open/stat/enumeration/delete calls retry bounded `EINTR`; each retried
  `unlinkat` is a fresh destructive boundary with its own cancellation check,
  while `close` is never retried. Keep
  actual deletion on `SecureRemovalBackend` for Windows, macOS,
  and Linux. Check cancellation immediately before each native delete and keep
  handle cleanup in `finally` paths. On Windows, the legacy
  `FileDispositionInfo` fallback is a second destructive call after
  `FileDispositionInfoEx`, so it needs its own cancellation gate; its
  `DeleteFile` field is a one-byte native `BOOLEAN`, while the API return value
  remains a four-byte `BOOL`. The managed traversal retained for dry-run
  counting is preview-only; an unavailable secure backend refuses real
  deletion with an actionable environment error and never falls back to
  `File.Delete`/`Directory.Delete`. POSIX `unlinkat` is parent-handle-relative
  but still names its final entry, so document rather than conceal its narrow
  final-name race for files, links, directories, and the selected root; the
  held-directory design prevents a replacement ancestor or child directory
  from redirecting recursive traversal, though an empty final-name replacement
  can be removed.
  Linux `struct stat` is architecture-specific: the secure backend explicitly
  selects the verified little-endian x64 or ARM64 layout and stays disabled on
  unverified architectures or endianness instead of interpreting arbitrary
  buffer offsets. macOS likewise supports only native little-endian ARM64: its
  unsuffixed libc symbols expose a different legacy inode ABI on x86_64, so
  Intel and Rosetta processes fail closed. Every captured Unix object identity also compares kernel-
  maintained change-time seconds/nanoseconds so immediate inode reuse cannot
  make a replacement link, parent, or selected directory look like the one
  validated earlier. A directory changed after validation is refused and must
  be revalidated. Directory final-name checks compare the
  current opened-descriptor stat to the named entry after traversal, because
  deleting children legitimately changes the directory's change time.
  Real directory execution fails closed without a pinned native identity.
  Empty-directory cleanup is a distinct execution contract: validation pins
  the identity and native traversal refuses immediately if it observes any
  child, so a directory populated after validation is not recursively cleaned.
  Real link-only execution likewise fails closed without a pinned canonical
  parent plus native parent/link identities. Broken-link cleanup and agent
  unlink actions revalidate both identities and delete through the opened
  parent/object boundary rather than trusting the current pathname.
  Cleanup batches validate lazily immediately before each target executes;
  prevalidating sibling links is incorrect because the first unlink changes
  their shared parent's generation and invalidates later captures.
  Unix destructive identity capture resolves only the parent, then opens the
  final candidate with `O_NOFOLLOW | O_DIRECTORY`; do not reuse final-following
  canonicalization for this purpose. Review each native operation as an
  observe/reopen/compare/delete chain and reject reconstructed child paths once
  a parent handle exists. Every platform comparison includes both its full
  native object ID and a change-time/generation signal for identities captured
  during validation. Transient traversal entries rely on parent-relative open
  plus full ID/type comparison because Windows directory-enumeration timestamps
  are not a stable policy-generation contract. Do not scope immediate ID reuse
  reasoning or tests to Unix. Regression coverage includes Windows
  ancestor replacement plus a matching hard link, final-component link
  replacement, and same-ID generation reuse on each supported OS.
  Validation also captures an object-local policy snapshot through the held
  directory: `SKILL.md`, `.git`, and emptiness are inspected relative to that
  handle/descriptor and bracketed by generation checks. Canonical pathnames are
  display/containment addresses, not substitutes for object-relative policy I/O.
  Windows rename behavior varies by filesystem: a rename may update
  `ChangeTime`, in which case validation must fail closed, or preserve it, in
  which case the returned policy snapshot must still describe the held object.
  Cross-platform ABA tests should assert those two safe outcomes rather than
  require one volume-specific timestamp behavior.
  Windows final-path normalization converts extended UNC paths and strips the
  extended prefix from drive-letter paths only. Preserve volume-GUID and other
  non-DOS extended namespaces so absolute authority paths never become relative.
  Broken-link cleanup observes target existence relative to the held parent and
  rechecks both parent and link identities around that observation, preventing a
  broken-to-valid replacement from being authorized.
  A non-mutating `remove` preview may use managed path inspection when the
  secure backend is unavailable, but it carries no execution identity and can
  never authorize deletion. Agent links outside inventory scan roots remain
  blocked with explicit `--scan-root` guidance; never promote an untrusted
  `gh skill list` path into a root automatically. Batch duplicate targets are
  reported as skipped so cleanup summaries do not mislabel them as failures.
  Cleanup resolves and deduplicates every selected path key before yielding the
  first lazy validation in both TUI and CLI apply flows, while still capturing
  each unique native identity only immediately before its removal. CLI duplicate
  candidates are reported as skipped and do not change a successful apply into
  an environment error. TUI removal attempt state retains that pre-validation
  skip count when cancellation throws before a batch report is returned, so
  canceled summaries do not reclassify duplicates as failures. The advanced remove wizard's finish-time
  revalidation compares the captured directory/link identities and removal mode
  as well as visible policy content; a replacement object always returns the
  user to Review rather than inheriting the earlier confirmation.
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
