# Adversarial concurrency, resource, cancellation, and cross-platform audit

Date: 2026-08-28
Branch reviewed: `feat/tui-usability-hardening`
Reviewed commit: `7d43336` (`fix: address concurrency review findings`)
PR follow-up reviewed: `df62a22` (`fix: close remaining lifecycle review gaps`),
merged by PR #11 as `40bbf0b`
Hardening branch: `fix/adversarial-hardening`
Second hardening branch: `fix/resource-lifecycle-hardening-2`
Third hardening branch: `fix/removal-lifecycle-hardening`
Status: Active remediation. PRs #12 and #13 are merged. Aggregate log retention,
callback-safe cancellation ownership, bounded/cancellable inventory I/O, and
portable asynchronous removal are implemented. Native handle-relative deletion
for hostile same-user mutation remains a separate security design; metadata
scheduling, install-modal ownership, root CLI cancellation, path identity, Esc/
LRU coverage, and the lower-risk remainder stay scheduled.

## Executive summary

The original repository-wide audit found 11 actionable issues: one critical,
four high, and six medium. The critical removal-boundary defect and the
shared-cache race were reproduced against the actual production classes rather
than inferred only from source inspection.

The PR follow-up exposed two misses in the original audit's UI-lifecycle model:
entering Doctor bypassed tab-load cancellation, and leaving Updates did not
cancel the active update operation. Both were fixed in `df62a22`. A targeted
reassessment then found three additional deferred production issues and one
test-coverage weakness: Discover and Doctor work still lacks workspace-scoped
ownership, log initialization/replay has two snapshot races, Esc does not leave
all advertised filter inputs, and the LRU test proves only FIFO eviction. These
are documented after Finding 11.

The critical and high findings should be treated as release blockers. The
medium findings should be addressed in the same hardening cycle because they
share lifecycle, cancellation, and bounded-resource concerns with the
release-blocking work.

The original audit made no production changes. Remediation is now in progress
on `fix/adversarial-hardening`; completed work and current verification are
recorded below.

## Scope and method

The review covered:

- All TUI shells, tabs, screens, dialogs, event handlers, and UI-dispatch paths.
- Application initialization, run-loop shutdown, cancellation, and disposal.
- Every asynchronous workflow and fire-and-forget call site.
- Cancellation source ownership and replacement/supersession helpers.
- `gh` process creation, output draining, cancellation, and termination.
- Inventory scanning, path normalization, symlink handling, cleanup, and removal.
- Logger retention, subscribers, disk sinks, rotation, and disposal.
- In-memory caches and shared mutable state.
- CLI cancellation and resource ownership.
- Windows, macOS, and Linux path, reparse-point, process, and file-sharing behavior.
- Existing unit/integration coverage and gaps around timing and high-volume inputs.

The following verification was performed:

- `dotnet build SkillView.sln --no-restore`: passed with zero warnings.
- The repository's configured analyzer/build gate passed with zero warnings.
  A later verification with the broader, non-configured `AnalysisLevel=latest-all`
  ruleset reported 190 warnings-as-errors across changed and unchanged code;
  that ruleset needs a separate baseline and triage before it can be treated as
  a meaningful regression gate.
- `dotnet test --no-build`: 556 of 556 tests passed.
- macOS ARM64 AOT publish: passed.
- Removal escape reproduction: confirmed with the real validator and removal service.
- Shared-cache stress reproduction: confirmed with 1,761 concurrent-operation exceptions.
- Repository working tree after the audit: clean.

Foreign Windows and Linux runtime packs were not restored locally, so this
audit does not claim live Windows/Linux runtime execution. Cross-platform
findings come from shared production code, the repository's platform CI
configuration, documented .NET 10 behavior, and a live macOS reproduction where
noted.

## Post-PR reassessment: why two lifecycle defects were missed

The original audit correctly examined the cancellation-slot implementation and
shutdown lifetime, but it did not model the complete UI as a transition graph.
That distinction matters because a view can stop owning the screen without
being disposed:

- `ActivateTab` performed tab cancellation, while `EnterDoctor` hid the same
  tabs through a different route.
- Updates canceled work on disposal, but Esc only hid the persistent embedded
  view; disposal never occurred.

The audit also overclaimed that the Updates operation slot prevented stale
operation results. It prevented two update operations from starting at the
same time while the view stayed active, but it did not tie the operation to
view deactivation. That statement has been corrected in the "Existing
hardening" section.

Future lifecycle reviews should require a transition matrix for every
workspace and modal. For each exit path, record:

1. Which view becomes hidden, stopped, or disposed.
2. Which load, operation, timer, subscription, and queued UI callback it owns.
3. Which token is canceled and which task is awaited.
4. Which generation or ownership check protects a late completion.
5. Which global UI state the completion can change: focus, status, spinner,
   preview, visibility, or enabled controls.

Deterministic tests should exercise each transition while the operation is held
at three boundaries: before cancellation, after background completion but
before UI dispatch, and after UI dispatch is queued but before it runs.

### PR #11 follow-up disposition

- **File sink lock inversion:** confirmed and fixed. Subscription deactivation
  now occurs outside the sink lock, with a deterministic deadlock regression
  test.
- **Doctor bypassed tab cancellation:** confirmed and fixed through the shared
  `CancelPendingTabWork` path.
- **Updates operation survived Esc/deactivation:** confirmed and fixed. Leaving
  the view now cancels the update, restores controls, and rejects a canceled
  operation's late success or error UI callback.

The focused follow-up passed all 559 local tests plus Linux, macOS, and Windows
tests, AOT smoke publishing on all three platforms, and CodeQL.

### Post-merge hardening checkpoint

The first natural implementation checkpoint completes remediation-order items
1-6:

1. Removal now uses an explicit, depth-bounded, cancellation-aware traversal
   with one lazy enumerator frame per active depth rather than materialized
   child arrays or pending sibling stacks.
   Nested symbolic links, junctions, mount-point reparse points, broken links,
   and cycles are deleted only as leaf links; containment and ancestor link
   state are revalidated immediately before each destructive operation. This
   closes the static packaged-link escape, but supported .NET path deletion is
   not atomic against a hostile same-user process replacing an ancestor between
   the last check and use; that stronger native design remains open.
2. `GhSkillListCache` serializes all state and shares one cancellable load per
   key. Invalidation cancels in-flight loads, rejects stale completion, and the
   final canceled waiter waits for subprocess cleanup.
3. `BackgroundTaskTracker` owns application fire-and-forget work. Shutdown
   closes admission, cancels app/workspace lifetimes, drains admitted work,
   and only then clears/disposes Terminal.Gui state. Direct UI dispatch remains
   available before the first real run for unit helpers but is permanently
   disabled after lifecycle entry.
4. Discover and Doctor now have activation lifetimes and generation checks.
   Search and preview are canceled on Discover exit, Doctor probes are canceled
   on Doctor exit, searches supersede earlier searches, opening logs cancels a
   preview that could close them, and environment probes are single-flight.
5. `Logger.SubscribeWithReplay` establishes an atomic, sequence-numbered
   replay/live boundary. Both the file sink and TUI use it, so concurrent
   entries cannot fall into a snapshot gap or appear twice.
6. Logs now have per-message and total-retained-character budgets, compact
   stderr excerpts, a character-bounded TUI queue, same-day size rotation,
   numbered file parts, active-file exclusion from trimming, and delete-share
   compatibility on Windows.

Local verification at this checkpoint:

- `dotnet build --no-restore`: passed with zero warnings.
- `dotnet test --no-build`: 613 of 613 tests passed, including the ANSI-driver
  integration suite.
- New deterministic tests cover removal escapes/cycles/retargeting,
  cancellation between entries in a 2,000-file directory, 100,000-operation
  cache contention, invalidation cleanup, shutdown drain, workspace exits,
  replay/live interleavings, message budgets, same-day file rotation, restart
  with an oversized active file, active-file retention, logger sequence-gap
  backpressure, search/preview commit invalidation, and parse-aware inventory
  caching.
- PR #12's initial Linux, macOS, and Windows tests, AOT smoke publishes on all
  three platforms, and CodeQL checks passed. Follow-up CI reruns after review
  fixes are recorded on the PR.

### PR #12 Copilot review disposition

The first Copilot review generated four comments. Each was independently
reassessed:

1. **Path check/use race — correct residual limitation, not representable as a
   portable managed fix.** The static nested-link escape remains fixed, but
   path revalidation is not atomic against hostile same-user mutation. The
   threat-model boundary, .NET runtime evidence, and native follow-up are now
   explicit in this report, `AGENTS.md`, and `RemoveService` documentation.
2. **Per-directory `ToArray()` — correct and fixed.** Traversal now advances a
   lazy enumerator one entry at a time and checks cancellation between
   `MoveNext` calls.
3. **Pending sibling stack — correct and fixed.** One enumerator frame is kept
   per active depth, so retained traversal state is O(depth), not proportional
   to all unvisited siblings across the active path.
4. **Cache invalidation releases waiters before loader cleanup — correct and
   fixed.** Invalidation marks and cancels a flight without completing it. The
   completion is published only after the loader returns from process
   kill/drain cleanup; a deterministic held-cleanup test proves the waiter
   remains incomplete until then.

The second Copilot review surfaced one inline finding and two suppressed
findings from unchanged lines. All three were independently reassessed and
accepted:

1. **Logger sequence-gap buffer — correct and fixed.** The per-observer
   `SortedDictionary` could grow without relation to the bounded ring when the
   thread owning sequence N paused before observer dispatch. Out-of-order
   deliveries now apply synchronous monitor backpressure until the missing
   sequence is delivered, so the logger retains no separate gap collection.
   Recursive writes from an observer to the same logger are rejected before
   assigning a sequence because they are incompatible with synchronous ordered
   delivery. A deterministic test pauses sequence N before registration,
   proves N+1 remains blocked, then verifies exact ordered delivery.
2. **Preview started from the old table during search — correct and fixed.** A
   search already canceled the preview active at submission, but a user could
   start another preview before the search completed. Successful result commit
   now invalidates the preview gate again before replacing the table, preventing
   that old-row preview from painting over the new result set.
3. **Malformed successful inventory output cached as empty — correct and
   fixed.** Parsing now carries an explicit success signal into
   `GhSkillListCache.LoadResult.ShouldCache`. Only a schema-valid payload,
   including a genuine empty `[]`, is cacheable. Blank, malformed, non-array,
   non-object-record, and missing-name payloads remain retryable.

The third Copilot review generated one inline finding and two suppressed
findings. They describe two underlying ownership defects and were accepted:

1. **Queued search/preview callbacks outlive request leases — correct and
   fixed.** Terminal.Gui 2.4.17 does not install a UI synchronization context,
   so continuations commonly reach `IApplication.Invoke` from a worker thread.
   The old fire-and-forget dispatch returned immediately, allowing each `using`
   request lease to dispose before the UI loop evaluated `IsCurrent`. Search
   and preview success, error, timeout, and cleanup paths now use an awaitable,
   cancellation-aware dispatch wrapper. The request method cannot exit and
   dispose its lease until the queued callback runs or its owning workspace is
   canceled.
2. **Preview cancellation can clear search-owned busy state — correct and
   fixed.** The global boolean spinner had no owner. Search and preview now
   register distinct monotonic busy operation IDs. Removing a preview owner
   restores the newest remaining operation, including its status text, so
   opening logs or completing a preview cannot clear a still-running search.
   Workspace deactivation retains an explicit clear-all path.

Deterministic coverage holds an awaitable dispatch callback outside the UI
loop and proves the request remains current until execution, then exercises the
real log-pane action while search and preview busy owners overlap and verifies
that search remains visible after preview cancellation.

The fourth Copilot review generated three inline findings. All three were
independently reassessed and accepted:

1. **Indirect logger recursion can deadlock A → B → A — correct and fixed.**
   A single thread-static logger identity detected only direct recursion. A
   callback on logger A could write to logger B, whose callback could then
   write back to A and wait forever on A's in-flight sequence. Observer
   delivery now maintains a reusable per-thread stack of every active logger,
   rejecting any direct or indirect callback cycle before assigning another
   sequence or mutating a ring. The stack avoids a per-entry hash-set
   allocation, and a bounded regression test proves the two-logger cycle
   returns without deadlock or an extra A entry.
2. **Canceled deferred Installed layout can be reported as `CRASH` — correct
   and fixed.** The three post-population width-stabilization passes are
   tracked background tasks. If app teardown canceled their queued UI dispatch,
   the cancellation previously escaped into `BackgroundTaskTracker` and was
   reported as an unhandled fault. The deferred operation now consumes
   `OperationCanceledException` only when its own view/app lifetime token is
   canceled; unrelated cancellation and faults still propagate. The other tab
   dispatch paths were rechecked and already catch cancellation through their
   linked load/operation lifetime tokens.
3. **Numeric `skillName` accepted and cached — correct and fixed.** The generic
   optional-field reader intentionally tolerates numeric versions, but it must
   not define record identity. The canonical or legacy name field must now be
   a nonblank JSON string. Numeric, null, Boolean, and canonical-invalid plus
   legacy-valid records are rejected as schema-incompatible and remain
   retryable instead of entering the cache.

The fifth Copilot review generated one inline finding and two suppressed
findings. All three were independently reassessed and accepted:

1. **Cross-thread logger cycles bypass a thread-local logger stack — correct
   and fixed.** With concurrent outer writes, thread 1 could hold logger A's
   registration lock and enter B while thread 2 held B's registration lock and
   entered A. Each thread's local stack contained only its own path, so neither
   detected the opposing lock cycle. Logger observers are production sinks,
   not producers, and none of SkillView's observers log. The contract now
   rejects every logger write made while any observer callback is active on
   that thread, before assigning a sequence or mutating a ring. A scalar
   thread-local depth counter avoids per-entry allocation. A synchronized
   two-producer regression reaches both callbacks concurrently and proves both
   outer writes finish without nested entries or deadlock.
2. **Search continuation dereferences a disposed Discover lifetime — correct
   and fixed.** Discover navigation exchanges, cancels, and disposes the
   workspace source. Search now safely captures a usable token value before
   its first await and uses only that struct for its request and every success,
   timeout, and error dispatch. A source disposed before capture is rejected.
3. **Preview has the same disposed-source race — correct and fixed.** Preview
   uses the same capture helper and stable token for request creation,
   cancellation classification, and all UI dispatches. The repository-wide
   source audit found no equivalent externally-disposed source dereference in
   the tab cancellation-slot paths; their leases retain source ownership until
   completion. Modal lifetime ownership remains separately tracked by Finding
   8 and is not conflated with this workspace-source race.

### PR #12 final review and post-merge analogous reassessment

The final Copilot review was labeled “needs a closer look” and contained one
suppressed finding. It was independently reassessed and accepted:

1. **Aggregate log usage can exceed the budget while the active part grows —
   correct and fixed on `fix/resource-lifecycle-hardening-2`.** The first append
   after writer open/rotation established retention, but subsequent appends did
   not revisit it until another rotation. Older parts could therefore remain
   while active growth pushed aggregate usage over 50 MiB. `FileLogSink` now
   keeps incremental retained-byte accounting and re-runs retention on the
   first append that crosses the budget. If the active part alone is oversized
   or an old part cannot be deleted, retry is deferred until bounded additional
   growth so logging does not enumerate the directory on every line. A
   regression test begins with old and active parts under budget, grows only the
   active part, and proves the old part is removed before rotation.

The earlier review misses were then used as bug-family prompts rather than
one-off patches. This found four report/code gaps:

1. **Cancellation callbacks ran under ownership locks — correct and fixed.**
   `CancellationTokenSourceSlot` and `LatestRequestGate` called `Cancel()` while
   holding their private gates. `Cancel()` synchronously invokes arbitrary
   registered callbacks; a callback that waits for another thread to release
   or query the lease creates the same lock/callback inversion class that the
   logger reviews exposed. Ownership is now published under the lock,
   cancellation runs outside it, and source disposal is deferred until an
   in-progress cancellation returns. Leases capture the token struct at
   construction. Deterministic tests have a cancellation callback wait for a
   different thread to dispose the lease and prove it completes while the
   callback is active.
2. **The disposed-source pattern remains present in install modals — already
   tracked, report sharpened.** All three install surfaces use `async void`
   handlers with a `using`-owned source and repeatedly access `lifetime.Token`
   after awaits. If the synchronous dialog run returns first, the source is
   disposed while the handler continues. This is the same failure family as the
   Discover continuation bug, but it belongs to the already-scheduled modal
   lifetime redesign in Finding 8 rather than this checkpoint.
3. **Cleanup classification had a second uncancellable lazy-directory walk —
   correct and fixed with Finding 6.** The original inventory finding focused
   on `LocalSkillScanner`; `CleanupClassifier` also enumerated scan-root
   children without a token and placed only enumerable creation, not
   `MoveNext`, inside its error boundary. Cleanup classification now checks its
   owning token throughout and contains iteration-time I/O failures.
4. **The cache invoked its injected clock while holding the cache lock —
   low production risk, fixed.** The production clock is `UtcNow`, but the
   abstraction allowed an injected delegate to re-enter or coordinate with
   cache operations under `_gate`. Time capture now occurs before lock entry;
   loader-completion clock faults are converted into the shared load outcome
   so waiters cannot be stranded. A deterministic callback test invalidates on
   another thread while the clock is running and proves the cache lock is free.

Second-branch local verification at this checkpoint:

- `dotnet build --no-restore`: passed with zero warnings and zero errors.
- `dotnet test --no-build`: 625 of 625 tests passed, including the ANSI-driver
  integration suite.
- macOS ARM64 AOT publish and `--version` smoke checks passed for both the
  standalone app and gh extension. The first standalone restore emitted a
  local NuGet vulnerability-cache access warning, but native compilation and
  execution completed; CI will repeat the clean three-platform matrix.

## Finding 1: recursive removal can delete outside the selected skill

Severity: **Critical**

Implementation status: **Static nested-link escape completed on
`fix/adversarial-hardening`; hostile concurrent path replacement remains open.**

Locations:

- `src/SkillView.Core/Inventory/RemoveService.cs`, recursive enumeration around lines 106-121.
- `src/SkillView.Core/Inventory/RemoveValidator.cs`, ancestor-link validation around lines 78-87.

### Current behavior

`RemoveService` recursively enumerates files and directories with:

```csharp
new EnumerationOptions
{
    RecurseSubdirectories = true,
    AttributesToSkip = 0,
    IgnoreInaccessible = true,
}
```

Setting `AttributesToSkip` to zero means reparse points are included. Recursive
.NET searches include symbolic links, junctions, and mounted-drive reparse
points. `RemoveService` then deletes every file returned by that traversal.

`RemoveValidator` checks whether an ancestor between the known scan root and
the selected target is a link that escapes the root. It does not inspect links
inside the selected skill. A skill can therefore pass every validation rule
and still contain a child link to arbitrary external content.

### Reproduction

An isolated tree was created with:

- A scan root containing a normal skill directory.
- A valid `SKILL.md` inside the skill.
- A nested directory symlink inside the skill pointing to an external directory.
- One file in the external directory.

The real `RemoveValidator.Validate` returned:

```text
allowed=True errors=0
```

The real `RemoveService.Remove` dry run returned:

```text
files=2 dirs=2
```

There was only one physical file owned by the skill (`SKILL.md`). The second
file counted for deletion was the external file reached through the nested
link. A non-dry run would call `File.Delete` on that external file.

### Impact

- A user-confirmed removal can cross the displayed and validated target boundary.
- A malicious or accidentally packaged skill can cause unrelated user data to be deleted.
- A link to an ancestor can create excessive recursive traversal, path growth,
  latency, and allocation before enumeration fails.
- Windows junctions and mounted paths are in scope in addition to POSIX links.

### Post-implementation concurrent-mutation boundary

PR #12's first Copilot review correctly identified that revalidating ancestors
immediately before `File.Delete` or `Directory.Delete` still leaves a
check-to-use instruction window. Another same-user process can replace an
ancestor with a link after the check and before the path-based operation.

The static malicious-package defect reproduced above is fixed: links already
present in the skill are treated as leaves and never traversed. The remaining
race is a different threat model. .NET 10 exposes no supported portable API for
handle-relative enumeration and deletion with no-follow semantics. Its own
`Directory.Delete(..., recursive: true)` implementation is path-based on both
[Unix](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Unix.cs#L478-L523)
and
[Windows](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Windows.cs#L307-L402),
so substituting that API would not close the window and would also lose
SkillView's cancellation and partial-failure behavior.

Do not describe the current checks as atomic against a hostile process running
as the same user. A stronger design requires audited native implementations
(`openat`/`unlinkat`-style traversal on Unix and handle-relative/open-reparse-
point deletion on Windows), plus platform-specific identity and race tests.
This remains grouped with removal I/O work because it materially changes the
filesystem abstraction and cross-platform test matrix.

### Required remediation

- Never recurse into entries with `FileAttributes.ReparsePoint`.
- Treat every nested link as a leaf and delete only the link itself.
- Revalidate canonical containment immediately before each destructive operation.
- Use an explicit bounded traversal so link handling and cancellation are visible.
- For a hostile same-user mutation threat model, design and audit native
  handle-relative/no-follow deletion separately on Unix and Windows.
- Add regression tests for:
  - External directory links.
  - External file links.
  - Broken links.
  - Links to ancestors/cycles.
  - Links whose targets change after validation.
  - Windows directory junctions.

## Finding 2: `FileLogSink.Dispose` can deadlock

Severity: **High**

Status: **Fixed in `df62a22` / PR #11.** The replay/attach race described in
Finding 13 remains deferred.

Locations:

- `src/SkillView.Core/Logging/FileLogSink.cs`, `Append` around lines 47-70.
- `src/SkillView.Core/Logging/FileLogSink.cs`, `Dispose` around lines 197-206.
- `src/SkillView.Core/Logging/Logger.cs`, observer invocation and deactivation around lines 107-135.

### Current behavior

`Logger.ObserverRegistration.Invoke` holds the registration lock while invoking
the sink callback. `FileLogSink.Append` then acquires the sink lock.

`FileLogSink.Dispose` acquires the sink lock first and disposes the logger
subscription while still holding it. Subscription disposal waits for any
in-flight observer invocation by acquiring the registration lock.

The resulting lock cycle is:

1. Logging thread acquires the logger registration lock.
2. Logging thread enters `Append` and waits for the sink lock.
3. Disposal thread holds the sink lock.
4. Disposal thread calls subscription disposal and waits for the registration lock.

Neither thread can proceed.

### Secondary disposed-state race

`Append` checks `_disposed` before entering the sink lock and does not recheck it
inside the lock. A direct call that observes `false`, pauses, and resumes after
disposal can call `EnsureWriter` and reopen the file after disposal completed.
The field is also neither volatile nor accessed exclusively under the lock.

### Impact

- Application shutdown can freeze indefinitely.
- CLI completion can hang in `EntryPoint` while disposing the file sink.
- A writer can be recreated after the sink is logically disposed.

### Required remediation

- Atomically detach/capture the subscription before acquiring the sink lock.
- Dispose the subscription without holding the sink lock.
- Mark the sink disposed under the sink lock.
- Recheck disposed state inside `Append` after acquiring the lock.
- Add deterministic tests that coordinate an in-flight append with disposal.
- Add a test proving no file is created or reopened after disposal returns.

## Finding 3: late background callbacks can mutate disposed UI

Severity: **High**

Implementation status: **Completed on `fix/adversarial-hardening`; aggregate
active-growth enforcement completed on `fix/resource-lifecycle-hardening-2`.**

Locations:

- `src/SkillView.Core/Ui/SkillViewApp.cs`, `RunAsync` teardown around lines 214-227.
- `src/SkillView.Core/Ui/SkillViewApp.cs`, `Invoke` around lines 2114-2142.
- `src/SkillView.Core/Ui/SkillViewApp.cs`, `RunBackground` around lines 2253-2283.

### Current behavior

`RunBackground` starts discarded `Task.Run` tasks. The application does not
keep a registry of those tasks and does not await them during shutdown.

`RunAsync` cancels the lifetime and then resets:

```csharp
_hasRunLifetime = false;
_runLifetime = null;
_app = null;
```

It then disposes the window and application. `Invoke` executes its action
directly whenever `_app` is null and `_hasRunLifetime` is false. That fallback
is useful for pre-run unit helpers but becomes unsafe after a real run.

Late startup probes, inventory scans, rescans, discovery work, search `finally`
blocks, and error handlers can therefore invoke UI actions directly from worker
threads after teardown began or completed.

### Impact

- Off-UI-thread Terminal.Gui mutation.
- Access to disposed controls.
- Timing-dependent `NotInitializedException`, `ObjectDisposedException`, or
  native terminal-driver failures.
- Post-shutdown tasks can continue using services while `EntryPoint` disposes
  the file logger.

### Required remediation

- Track every application-owned background task.
- Stop accepting new tasks once shutdown begins.
- Cancel the lifetime and await all tracked tasks before clearing `_app` or
  disposing the window/application.
- Make the direct `Invoke` fallback valid only before the first real run, never
  after the application has entered its lifecycle.
- Add tests that hold startup/search/rescan work across cancellation and verify
  no callback touches UI after teardown.

## Finding 4: the shared `gh skill list` cache is not thread-safe

Severity: **High**

Implementation status: **Completed on `fix/adversarial-hardening`.**

Locations:

- `src/SkillView.Core/Gh/GhSkillListCache.cs`, all operations around lines 10-45.
- `src/SkillView.Core/Gh/GhSkillListAdapter.cs`, shared adapter/cache ownership.
- `src/SkillView.Core/Ui/TuiServices.cs`, singleton service composition.

### Current behavior

The cache uses a normal `Dictionary<string, CacheEntry>` with no lock.
`TryGet`, `Store`, and `Invalidate` all mutate or read it directly.

One adapter instance is shared across the application. Overlapping startup
inventory capture, tab loads, scope changes, update/install rescans, cleanup,
and explicit invalidation can access the dictionary concurrently.

### Reproduction

The actual internal cache was exercised with 250,000 mixed parallel `TryGet`,
`Store`, and `Invalidate` calls. It produced 1,761 exceptions. The first was:

```text
System.ArgumentException: Destination array was not long enough. Check the
destination index, length, and the array's lower bounds.
```

### Impact

- Normal overlapping TUI activity can crash the application.
- Cache state can be corrupted or entries can be lost.
- Simultaneous misses also spawn duplicate `gh skill list` processes.

### Required remediation

- Protect the complete cache state with one lock or use a concurrency-safe
  cache implementation with equivalent atomic expiry behavior.
- Add per-key single-flight loading so concurrent misses share one process.
- Keep invalidation atomic with respect to reads and stores.
- Add high-contention tests for get/store/invalidate and simultaneous expiry.

## Finding 5: log retention can consume multi-gigabyte memory and unbounded disk

Severity: **High**

Implementation status: **Completed on `fix/adversarial-hardening`.**

Locations:

- `src/SkillView.Core/Logging/Logger.cs`, entry retention around lines 36-55.
- `src/SkillView.Core/Subprocess/ProcessRunner.cs`, per-stream cap around lines 10 and 121-165.
- `src/SkillView.Core/Gh/GhSkillListAdapter.cs` and other `gh` adapters that log full stderr.
- `src/SkillView.Core/Logging/FileLogSink.cs`, trim scheduling around lines 104-158.
- `src/SkillView.Core/Ui/SkillViewApp.cs`, visible log queue around lines 1904-1953.

### Memory behavior

The logger limits only the number of entries. It does not limit message length
or total retained characters. `ProcessRunner` allows 1,048,576 characters per
captured stream, and multiple `gh` services interpolate the full stderr string
into warning entries.

At the default 2,048 entries, repeated noisy failures can retain approximately
4 GiB of UTF-16 character storage before accounting for:

- String and linked-list overhead.
- Regex redaction copies.
- Interpolated-string copies.
- Process-result strings.
- File formatting.
- The 512-entry visible log queue, which can retain roughly another 1 GiB of
  formatted copies while the log pane is open.

### Disk behavior

The documented 50 MB budget is checked only when a writer is first opened or
rotated. A long-running same-day process can grow the active file without any
further budget check.

If the application restarts on the same day with an already oversized current
file, trimming can select the active file:

- Unix can unlink it while the writer continues writing to an invisible open file.
- Windows deletion fails because the file was opened with `FileShare.Read`, not
  delete sharing.

### Impact

- Sustained failing commands can cause out-of-memory termination.
- The log pane magnifies retained memory.
- Logs can consume disk beyond the advertised budget.
- Unix and Windows behave differently when trimming the current file.

### Required remediation

- Apply a strict per-message character limit before inserting into the ring.
- Bound the ring by total retained characters/bytes in addition to entry count.
- Log concise stderr snippets rather than full captured streams.
- Bound visible-log text by both line count and total characters.
- Rotate files by size as well as date.
- Exclude the active file from deletion and rotate/close it before enforcing budget.
- Add same-day growth, restart, Windows sharing, and large-message tests.

## Finding 6: inventory scanning blocks the UI and largely ignores cancellation

Severity: **Medium**

Implementation status: **Completed on
`fix/resource-lifecycle-hardening-2`.**

Locations:

- `src/SkillView.Core/Inventory/LocalInventoryService.cs`, `CaptureAsync` around lines 42-81.
- `src/SkillView.Core/Inventory/LocalSkillScanner.cs`, enumeration and reads around lines 48-124.
- `src/SkillView.Core/Inventory/SkillLockFileReader.cs`, full-file read around line 84.

### Current behavior

`LocalInventoryService.CaptureAsync` performs root resolution and the full
filesystem scan before reaching its first `await`. When an environment report
is already cached, tab activation reaches this scan synchronously on the UI
thread before the returned task can yield.

The scanner does not accept a cancellation token. It reads every `SKILL.md`
completely, even though inventory needs only front matter. `SkillLockFileReader`
also reads complete files into byte arrays without a size limit.

`Directory.EnumerateFileSystemEntries` is lazy. The code catches exceptions
around creation of the enumerable but performs `foreach` outside that catch.
ACL changes, directory removal, disconnection, or other errors raised during
iteration can escape and abort the scan.

### Remediation checkpoint

`LocalInventoryService` now schedules root resolution, filesystem scanning,
and package-lock enrichment on the thread pool and runs that work concurrently
with `gh skill list`. Cancellation is checked between roots, candidates,
bounded read chunks, lockfiles, and cleanup-classification entries.

`LocalSkillScanner` reads at most 64 KiB from each `SKILL.md`, enough for normal
front matter without allocating the entire third-party document.
`SkillLockFileReader` rents a bounded buffer, reads at most 1 MiB plus one byte,
and rejects oversized manifests. Both use delete-sharing so concurrent package
updates/removal do not unnecessarily pin files on Windows. Lazy enumeration
now catches failures from the actual `MoveNext` call, not only enumerable
creation. Focused tests cover mid-scan cancellation, a closing front-matter
fence beyond the byte bound, oversized lockfiles, cancellation before lockfile
I/O, and a simulated iteration-time disconnect.

### Impact

- Frozen TUI during installed/changes/updates refreshes.
- Cancellation does not stop local I/O promptly.
- A huge third-party `SKILL.md` or lock file can cause large allocations.
- Network mounts, removable media, and Windows ACL behavior increase latency
  and failure likelihood.

### Required remediation

- Execute filesystem scanning away from the UI thread.
- Pass a cancellation token through resolver/scanner/lock-reader operations.
- Check cancellation per root and candidate.
- Read a bounded front-matter prefix rather than the complete Markdown file.
- Establish and enforce a lock-file size limit.
- Catch errors around actual enumeration/move-next operations.
- Add cancellation, oversized-file, disappearing-directory, ACL, and network-like
  slow-I/O tests.

## Finding 7: agent-filtered search can launch 200 sequential processes without timeout

Severity: **Medium**

Implementation status: **Partially completed.** New searches now supersede the
previous request and the whole search has a two-minute deadline. Per-metadata
preview deadlines and bounded scheduling remain in remediation item 8.

Locations:

- `src/SkillView.Core/Gh/GhSkillSearchService.cs`, maximum result limit around lines 15-16.
- `src/SkillView.Core/Ui/SkillViewApp.cs`, search state around lines 961-1035.
- `src/SkillView.Core/Ui/SkillViewApp.cs`, metadata filtering around lines 1040-1092.

### Current behavior

When an agent filter is supplied, SkillView previews each uncached search result
to read front-matter agent metadata. The configured maximum is 200 results, so
one search can execute 200 sequential `gh skill preview` processes.

Selected-item preview has a 30-second timeout, but metadata previews use only
the application lifetime token. One stuck command prevents the search from
completing indefinitely. While `_searching` is true, a later user search is
rejected rather than canceling and superseding the stale request.

### Impact

- Very high search latency and process churn.
- A single hung preview blocks search until application shutdown.
- Users cannot correct/refine a slow search without quitting.

### Required remediation

- Give search its own latest-request gate and deadline.
- Cancel and supersede the previous search when a new one is submitted.
- Apply a timeout to every metadata preview.
- Prefer bulk metadata from `gh` if/when available; otherwise use tightly
  bounded concurrency and rate-aware scheduling.
- Preserve the bounded LRU metadata cache.
- Add timeout, supersession, partial-failure, and 200-result tests.

## Finding 8: install modal async handlers can outlive disposed dialogs

Severity: **Medium**

Implementation status: **Install flows remain open. The removal modal/wizard
now demonstrate the target lifecycle on `fix/removal-lifecycle-hardening`:
stable token capture, an explicitly retained operation task, inner queued-
callback lifetime checks, cancellation, and task drain before control
disposal.**

Locations:

- `src/SkillView.Core/Ui/InstallConfirmModal.cs`, async accepting handler around lines 189-264.
- `src/SkillView.Core/Ui/RepoSkillPickerModal.cs`, async accepting handler around lines 249-375.
- `src/SkillView.Core/Ui/InstallScreen.cs`, async accepting handler around lines 394-451.

### Current behavior

The Terminal.Gui accepting events use `async void` handlers whose tasks are not
owned or awaited by the modal. `Ctrl+Q` can request stop for both the top modal
and the main window while an install is active.

The compact and picker handlers check cancellation before calling
`IApplication.Invoke`, but the invocation is queued when called from a worker
thread. They do not recheck the dialog lifetime inside the queued callback.
The dialog can close and be disposed between the check and callback execution.

An exception thrown by a UI invocation from inside a catch block is outside the
original try/catch and can escape the `async void` handler.

`InstallScreen` has an inner cancellation check and is safer, but still does not
own/await the operation and can encounter application-disposal races.

The later disposed-source reassessment adds a second concrete failure mode:
each handler repeatedly reads `lifetime.Token` after awaits even though
`lifetime` is a `using` local owned by the synchronous `Show` method. If the
dialog stops and `Show` returns while its `async void` handler is still active,
the source is disposed and a continuation can throw `ObjectDisposedException`
while merely evaluating the token, including from an exception/cancellation
path. The modal fix must capture a stable token before the first await in
addition to owning and awaiting the operation task.

### Impact

- Queued callbacks can update disposed controls.
- Async-void exceptions can reach the application/thread exception path.
- Shutdown may return while install code still owns process and UI references.

### Required remediation

- Track the modal's active operation task explicitly.
- Recheck lifetime inside every queued UI action.
- Observe and contain all UI-dispatch exceptions.
- Cancel and await the operation before disposing the dialog.
- Consider an asynchronous modal lifecycle rather than synchronous `Run` plus
  untracked event tasks.
- Add Ctrl+Q, Esc, completion-vs-close, and exception-vs-dispose race tests.

## Finding 9: CLI cancellation and process termination are incomplete

Severity: **Medium**

Locations:

- `src/SkillView.Core/Bootstrapping/EntryPoint.cs`, `RunAsync` around line 12.
- `src/SkillView.Core/Cli/CliDispatcher.cs`, `RunAsync` around line 23 and service calls throughout.
- `src/SkillView.Core/Subprocess/ProcessRunner.cs`, cancellation handling around lines 68-80.

### Current behavior

Neither the shared entrypoint nor CLI dispatcher accepts an application
cancellation token. CLI service calls therefore use default tokens and rely on
abrupt process termination when Ctrl+C is pressed.

The TUI process runner calls `Kill(entireProcessTree: true)` after cancellation,
but does not wait for the process after issuing the kill. `.NET` process kill
is asynchronous, and parent exit status does not prove all descendants have
exited.

Most `gh` operations also lack operation-specific deadlines; selected preview
is the notable exception.

### Impact

- CLI commands cannot perform cooperative cleanup.
- Long-running/hung `gh` calls have no uniform deadline.
- Cancellation can return before the child process has actually exited.
- Descendants can survive best-effort tree termination.

### Required remediation

- Create a root cancellation source in `EntryPoint`.
- Translate `Console.CancelKeyPress` into cancellation and restore/unsubscribe
  the handler during teardown.
- Propagate the token through `CliDispatcher` and every service call.
- Apply operation-specific timeouts.
- After `Kill`, perform a bounded wait for parent exit and log/report failures.
- Add cross-platform Ctrl+C and child-process cancellation tests.

## Finding 10: removal materializes full trees and runs on the UI thread

Severity: **Medium**

Implementation status: **Completed on `fix/removal-lifecycle-hardening`. The
portable path remains non-atomic against hostile same-user path replacement,
as documented under Finding 1.**

Locations:

- `src/SkillView.Core/Inventory/RemoveService.cs`, `.ToList()` traversal around lines 106-121.
- `src/SkillView.Core/Ui/RemoveConfirmModal.cs`, synchronous remove around line 139.
- `src/SkillView.Core/Ui/RemoveScreen.cs`, synchronous execute around lines 268-287.
- `src/SkillView.Core/Ui/CleanupScreen.cs`, synchronous batch removal around lines 179-241.

### Current behavior

The original implementation materialized every file path and directory path
before deletion and ran synchronously from Terminal.Gui event handlers. The
earlier branch replaced that traversal with O(depth) enumerator frames. This
branch completes the remediation: compact remove, advanced remove, cleanup
batches, cleanup validation, and agent-link unlinking run off the UI thread;
Esc cancels active work; progress is throttled to 10 updates per second; and
each owning window cancels and drains its task before disposing controls.

Cancellation publishes exact aggregate progress, including cancellation
between batch targets. The workflow invalidates and rescans inventory whenever
even a partial target deleted files or directories. Runtime failure detail is
bounded to 128 messages plus an omission summary while `ErrorCount` preserves
the exact total.

### Impact

- Large trees allocate proportional path lists before deletion begins.
- Slow local disks, antivirus, network mounts, or large packages freeze the UI.
- Users cannot cancel after confirming.
- Link cycles amplify both traversal time and memory until failure.

### Required remediation

- First apply the critical reparse-point fix.
- Replace complete file/dir materialization with an explicit bounded traversal.
- Make removal asynchronous and cancellation-aware.
- Report progress through throttled UI dispatch.
- Preserve bottom-up directory deletion without retaining every file path.
- Add large-tree, slow-I/O, cancellation, and partial-failure tests.

## Finding 11: path identity is wrong on case-insensitive filesystems

Severity: **Medium**

Locations:

- `src/SkillView.Core/Inventory/PathResolver.cs`, containment around lines 69-79.
- `src/SkillView.Core/Inventory/ScanRootResolver.cs`, root deduplication around lines 43-46.
- `src/SkillView.Core/Inventory/LocalInventoryService.cs`, merge indexes.
- Other cleanup and removal sets keyed by normalized path with `StringComparer.Ordinal`.

### Current behavior

Path normalization changes separators and trims trailing separators, but it
does not canonicalize case. Path equality, containment, deduplication, and merge
keys use ordinal case-sensitive comparisons.

On normal Windows filesystems, `C:\Users\X` and `c:\users\x` identify the same
path but SkillView treats them as unrelated. Case-insensitive macOS volumes have
the same class of problem. Linux filesystems are normally case-sensitive, and
Windows can enable case sensitivity per directory, so an unconditional global
ignore-case comparer is also not a complete solution.

### Impact

- Legitimate containment validation can fail.
- The same physical root or skill can appear more than once.
- `gh skill list` records may not merge with filesystem records.
- Cleanup classification can report false duplicates or anomalies.

### Required remediation

- Centralize path identity rather than selecting comparers independently.
- At minimum use OS-appropriate ordinal comparison consistently.
- Where destructive identity matters, prefer canonical filesystem identity or
  independently revalidated containment rather than string equality alone.
- Add Windows mixed-case, macOS case-insensitive-volume, Linux case-sensitive,
  and Windows case-sensitive-directory tests.

## Finding 12: Discover and Doctor operations outlive workspace ownership

Severity: **High**

Implementation status: **Completed on `fix/adversarial-hardening`.**

Locations:

- `src/SkillView.Core/Ui/SkillViewApp.cs`, `ActivateTab` and `EnterDoctor`.
- `src/SkillView.Core/Ui/SkillViewApp.cs`, `RunSearchAsync` around lines 967-1044.
- `src/SkillView.Core/Ui/SkillViewApp.cs`, `PreviewSelectedAsync` around lines 1100-1223.
- `src/SkillView.Core/Ui/SkillViewApp.cs`, Doctor probing around lines 764-800.

### Current behavior

The post-PR shared cancellation helper covers Installed, Changes, and Updates.
It does not cancel work owned by Discover or Doctor:

- Discover search uses only the application lifetime. Switching to Installed,
  Changes, or Doctor leaves the search and up to 200 agent-metadata preview
  subprocesses running. A late success replaces hidden Discover state, calls
  `SetFocus` on the results table, changes the global status, and clears the
  shared spinner.
- Selected preview has its own latest-request gate, but leaving Discover does
  not cancel it. Its success callback calls `ShowPreviewPane`, which can close
  a log pane the user opened while preview was in flight and changes global
  status/spinner state after another workspace became active.
- Starting a new search does not cancel an in-flight selected preview for the
  old result set. The old preview can therefore overwrite the preview/title
  after the new search results have been installed.
- Doctor probing uses the application lifetime and `RunBackground`. Leaving
  Doctor before the probe completes does not cancel or supersede it. The late
  callback updates the hidden Doctor view and clears the shared spinner.
- Startup, Doctor, and coordinator paths can all observe `_lastReport == null`
  and launch duplicate environment probes rather than sharing one in-flight
  task.

Terminal.Gui currently rejects `SetFocus()` when a view or ancestor is hidden,
so the hidden-focus calls are less damaging than they appear. The remaining
status, spinner, preview-mode, process, and hidden-state mutations are still
real application-level lifecycle defects.

### Impact

- Hidden work consumes `gh` processes, filesystem I/O, and memory after the
  user has left the feature.
- One workspace can clear or overwrite another workspace's progress and status.
- A selected preview from the old result set can be displayed beside new search
  results.
- Returning from logs or another tab can reveal state chosen by a stale
  completion rather than the user's latest action.

### Required remediation

- Give every workspace an explicit activation lifetime, separate from the
  application lifetime.
- Cancel Discover search and selected preview when Discover deactivates.
- Cancel or generation-gate Doctor probing when Doctor deactivates.
- Include workspace/generation checks inside the queued UI callback, not only
  before calling `Invoke`.
- Separate progress ownership so one operation cannot clear another operation's
  spinner/status.
- Store and share one in-flight environment-probe task.
- Add transition tests for Discover to every primary tab, Discover to Doctor,
  preview to logs, new-search versus old-preview completion, and Doctor leave
  versus probe completion.

## Finding 13: log snapshot/replay initialization can lose or duplicate entries

Severity: **Medium**

Implementation status: **Completed on `fix/adversarial-hardening`.**

Locations:

- `src/SkillView.Core/Ui/SkillViewApp.cs`, `OnLogEntry` and
  `InitializeVisibleLogLines` around lines 1909-1941.
- `src/SkillView.Core/Logging/FileLogSink.cs`, `Attach` around lines 44-55.
- `src/SkillView.Core/Logging/Logger.cs`, separate ring and observer locks.

### New Copilot "needs a closer look" comment

Copilot's
[post-fix review](https://github.com/harder/gh-skillview/pull/11#pullrequestreview-5052520853)
correctly identified this visible-log loss interleaving:

1. `InitializeVisibleLogLines` takes `Logger.Snapshot()`.
2. A new log entry is added and `OnLogEntry` enqueues it.
3. Initialization acquires `_visibleLogGate`, clears the queue, and replaces it
   with the older snapshot.

The new entry is permanently absent from the visible log pane until the pane is
closed and initialized again.

The suggested minimal change—hold `_visibleLogGate` while taking the logger
snapshot—prevents that loss but needs closer design. `Logger.Log` commits the
ring entry before it invokes observers. A log thread can add an entry, block in
`OnLogEntry` on `_visibleLogGate`, and then have initialization include that
same entry in the snapshot. When the gate is released, the callback enqueues it
again, producing a duplicate.

`_showingLogs` is also written on the UI thread and read by logger-callback
threads without a lock or volatile access. The callback can observe stale pane
visibility while deciding whether to retain and schedule an entry.

### Analogous file-sink race found during reassessment

`FileLogSink.Attach` replays `logger.Snapshot()` and subscribes afterward. A log
emitted between those calls is in neither the replay nor the new subscription,
so it is absent from the disk log. Reversing the order to subscribe first and
snapshot second creates a duplicate window instead of closing the race.

The public `Attach` method can also race re-attach or disposal; its one-shot
startup assumption is not expressed or enforced by the API.

### Required remediation

- Define a logger sequence number or an atomic replay-subscribe operation.
- Commit ring insertion and observer membership against one ordering boundary,
  then deliver replay/live entries with a watermark so each sequence appears
  exactly once.
- Use the same primitive for the file sink and visible log pane rather than
  maintaining two subtly different replay protocols.
- Synchronize pane visibility with queue state, or enqueue all bounded entries
  regardless of visibility and only coalesce drawing while visible.
- Make file-sink attachment one-shot and startup-only, or fully synchronize
  attach/re-attach/dispose.
- Add deterministic tests for logs emitted before snapshot, between snapshot
  and subscription/replacement, and after subscription, asserting no gaps and
  no duplicates.

## Finding 14: Esc does not leave all Discover filter inputs as advertised

Severity: **Medium usability/reliability**

Location:

- `src/SkillView.Core/Ui/SkillViewApp.cs`, root Esc handling around lines 598-607.

### Current behavior

Copilot also recorded this in the
[preceding review's suppressed follow-ups](https://github.com/harder/gh-skillview/pull/11#pullrequestreview-5052291512).
The query field handles Esc locally by returning focus to the results table.
The owner field, agent field, and numeric limit control do not. Their Esc key
reaches the root handler, which consumes it and displays "Esc leaves the field"
without actually changing focus. The next plain `q` is consequently typed into
the field rather than quitting. `Ctrl+Q` remains the unconditional escape hatch,
but the UI message and expected two-step Esc then q behavior are wrong.

### Required remediation

- Centralize `LeaveTextInput` and call it for every editable Discover/Installed
  control before consuming Esc.
- Test query, owner, agent, numeric limit, and Installed filter focus separately.
- Keep the application-level Ctrl+Q test because focused editors are precisely
  where global quit routing tends to regress.

## Test gap: the LRU test currently proves only FIFO eviction

`Store_EvictsLeastRecentlyUsedMetadataAtCapacity` inserts first, second, and
third without touching an older entry before eviction. A FIFO cache would pass
the same test. The implementation's `Has` method does call `Touch`, so this is
not evidence of a production defect, but the contract is unprotected. Touch
`first` before storing `third` and assert that `second` was evicted. Also test
that updating an existing value refreshes recency.

## Lower-risk hardening opportunities

These should be included while the related components are being changed:

1. [x] Validate that `Logger` capacity is non-negative; completed in PR #12.
2. Serialize tests that mutate `TuiHelpers.CurrentTheme`, Terminal.Gui scheme
   facades, or other static UI configuration.
3. Stream CLI JSON directly to `Console.Out` rather than creating a
   `MemoryStream`, copying it to a byte array, decoding to UTF-16, and encoding
   it again for output.
4. Add explicit concurrency coverage for:
   - Cache get/store/invalidate.
   - Logger sink append/dispose.
   - Logger replay/subscribe without gaps or duplicates.
   - UI shutdown with pending startup/search/rescan tasks.
   - Every workspace exit with pending load/operation/UI dispatch.
   - Modal shutdown with active installs.
   - Same-day log growth and active-file rotation.
5. Add stress tests with bounded memory assertions for noisy subprocess errors,
   oversized local files, large inventories, and large removal trees.

## Existing hardening that held up

The following areas were reviewed and found to be appropriately bounded or
coordinated within their current scope:

- `LatestRequestGate` correctly supersedes selected previews and applies their timeout.
- Installed and Changes cancellation slots prevent stale loads from replacing
  newer state. Updates now does the same for both loads and operations after
  `df62a22`; this was not true at the original audit commit.
- `ProcessRunner` drains stdout and stderr concurrently and bounds each captured stream.
- Search agent metadata uses a locked, capacity-limited LRU.
- Visible-log redraws are coalesced; this branch adds both line-count and total
  character budgets while retaining entries regardless of pane visibility.
- Terminal escape sanitization is stateless and applied to remote/untrusted
  rendered content.
- The application uses Terminal.Gui's modern `Application.Create().Init()` and
  `IApplication.Dispose()` lifecycle.
- External TUI cancellation is connected to `IApplication.RunAsync`; this
  branch adds ownership and shutdown quiescence for subordinate application
  tasks, while modal-specific lifetimes remain remediation item 10.

## Recommended remediation order

1. [x] Fix static nested-link traversal before any further release or removal testing.
2. [x] Make `GhSkillListCache` thread-safe and single-flight.
3. [x] Add tracked background-task ownership and shutdown quiescence.
4. [x] Add workspace activation lifetimes for Discover and Doctor, including
   search/preview/probe cancellation and generation checks.
5. [x] Replace log snapshot-plus-subscribe/replacement with exact-once replay.
6. [x] Add log character/byte budgets and correct disk rotation.
7. [x] Enforce aggregate disk retention during active-file growth and remove
   cancellation-callback execution from request/slot ownership locks.
8. [x] Move inventory and removal I/O off the UI thread with cancellation, and
   evaluate native handle-relative deletion for hostile same-user mutation.
   The portable inventory/removal work is complete. The native evaluation
   confirmed that supported .NET 10 APIs cannot make path validation and
   deletion atomic; an audited Unix/Windows native implementation remains a
   separate Finding 1 security follow-up rather than an implicit portable fix.
9. [ ] Finish metadata-preview deadlines and bounded scheduling (search
   supersession and a whole-request deadline are complete).
10. [~] Make modal operation lifetimes awaitable and disposal-safe, including
    stable token capture before the first await. Removal modals are complete;
    the three install flows remain.
11. [ ] Wire root CLI cancellation and bounded post-kill waiting.
12. [ ] Centralize cross-platform path identity semantics.
13. [ ] Correct Esc focus behavior and strengthen the LRU contract test.
14. [ ] Finish the lower-risk hardening and stress coverage.

`FileLogSink` lock ordering, Doctor-to-tab cancellation, and Updates operation
deactivation were completed in `df62a22` and are not in the remaining order.

## Terminal.Gui upstream assessment

Most SkillView findings belong in SkillView rather than Terminal.Gui. The
library cannot know that hiding Discover should cancel a `gh` process, and it
does not own SkillView's logger, filesystem traversal, path identity, cache,
or process-output policy. Terminal.Gui already rejects `SetFocus()` when a view
or an ancestor is hidden, removes `SpinnerView` timers during disposal, exposes
application-level keyboard routing, and provides `Markdown.ShowHeadingPrefix`.

The audit did identify the following upstream defects or worthwhile platform
improvements. They were checked against the current `develop` source at
`48efa0c5` and stable `v2.4.17`.

### TG-1: `TimedEvents.RunTimers` still executes callbacks under its queue lock

Priority: **High; new upstream issue recommended**

`TimedEvents.RunTimers` takes `_timeoutsLockToken` and calls `RunTimersImpl`.
`RunTimersImpl` takes the same re-entrant lock, removes one timeout, exits only
the inner lock, and invokes the user callback. Despite its comment saying the
callback executes outside the lock, the outer `RunTimers` lock remains held.
This exists in both stable `v2.4.17` and current `develop`.

A timeout callback that waits for background work which calls `AddTimeout`,
`RemoveTimeout`, or `IApplication.Invoke` can deadlock: the UI callback waits
for the worker, while the worker waits for the timeout lock held by the UI
callback. Long callbacks also block all cross-thread UI dispatch and timer
cancellation.

Do not simply remove all serialization because `RunTimers` is public and two
callers could then execute callbacks concurrently. Use a separate runner gate
or interlocked single-runner guard, while retaining the short queue lock only
for selecting/removing/reinserting entries. Add a deterministic test whose
callback waits for a background `Add`/`Remove`; it must complete without either
operation running concurrently with another timer callback.

No existing issue located by `TimedEvents`, `RunTimers`, lock, or deadlock
search describes this exact retained outer-lock problem.

### TG-2: complete the synchronization-context session fix before v2.5

Priority: **High; already tracked**

Stable `v2.4.17` installs a plain `SynchronizationContext`, so `await` in an
event handler resumes on the thread pool. The current multitasking guide says
the continuation automatically returns to the main thread, which is not true
for the latest stable release. Issue
[tui-cs/Terminal.Gui#5579](https://github.com/tui-cs/Terminal.Gui/issues/5579)
and PR
[tui-cs/Terminal.Gui#5588](https://github.com/tui-cs/Terminal.Gui/pull/5588)
introduced a real `MainLoopSyncContext` on `develop`.

That change created the startup/session deadlock tracked by
[tui-cs/Terminal.Gui#5636](https://github.com/tui-cs/Terminal.Gui/issues/5636).
PR
[tui-cs/Terminal.Gui#5641](https://github.com/tui-cs/Terminal.Gui/pull/5641)
contains the session-scoping and after-session fallback, but it was merged into
a stacked feature branch rather than current `develop`; the issue remains open.
Treat integration of that fix, including nested-session tests, as a v2.5
release gate. Version the multitasking documentation or clearly describe the
different stable and upcoming behavior until then.

### TG-3: the deeper `IApplication.Invoke` lifecycle race remains unresolved

Priority: **High; new or reopened upstream issue recommended**

[Issue #5163](https://github.com/tui-cs/Terminal.Gui/issues/5163) identified
that the fast path reads `TopRunnableView` and `MainThreadId` without
synchronizing against shutdown. PR
[#5185](https://github.com/tui-cs/Terminal.Gui/pull/5185) added a useful
pre-init/post-dispose `Initialized` guard, but explicitly left the deeper race
out of scope while closing the issue. The current implementation still permits
state to change after the guard or fast-path check and before action invocation
or timeout enqueue.

Re-open the concurrency portion as its own issue. Define whether a concurrent
shutdown causes the action to run, be canceled, or throw, then make the
decision atomic. Cover both the immediate UI-thread path and queued worker path
with deterministic lifecycle hooks rather than timing-only stress tests.

### TG-4: add awaitable, cancellable, session-aware UI dispatch

Priority: **Medium API improvement**

`IApplication.Invoke` returns `void`; callers cannot await execution, attach a
cancellation token, distinguish "queued" from "ran", or bind the callback to
the `IRunnable`/modal that owns the referenced views. SkillView had to build a
`TaskCompletionSource` wrapper and still implement all view-lifetime checks
itself.

Consider an `InvokeAsync(Action, CancellationToken)` API and a runnable/session
lifetime token that is canceled when that session ends. The callback should
recheck the token on the UI thread immediately before invoking user code.
Document how it behaves after session end and application disposal. This would
not automatically cancel application business operations, but it would make
safe modal/workspace dispatch a standard pattern.

The current multitasking guide and UICatalog threading scenario rely heavily on
`async void` event handlers and show cancellation without tying it to
`Disposing` or `IRunnable.IsRunningChanged`. Add an explicit async-modal example
that cancels and observes work before disposing referenced views. Longer term,
an async command/event abstraction could observe handler tasks and route their
exceptions through the application error handler.

### TG-5: add debug-time UI-thread/lifetime diagnostics

Priority: **Medium diagnostic improvement**

Terminal.Gui documents off-thread view mutation as undefined behavior, but most
view property mutations do not assert thread affinity. An opt-in debug guard or
an analyzer could flag mutation when `View.App.MainThreadId` differs, and warn
when an async event handler captures a view without cancellation on disposal.
This would have surfaced several SkillView audit findings much earlier without
adding release-build overhead.

## Primary platform references

- .NET recursive search and reparse points:
  <https://learn.microsoft.com/en-us/dotnet/api/system.io.searchoption?view=net-10.0>
- .NET directory enumeration:
  <https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.enumeratefiles?view=net-10.0>
- .NET process termination:
  <https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0>
- .NET console cancellation:
  <https://learn.microsoft.com/en-us/dotnet/api/system.console.cancelkeypress?view=net-10.0>
- Windows file-sharing/delete behavior:
  <https://learn.microsoft.com/en-us/windows/win32/fileio/file-streams>
- Windows/Linux case sensitivity:
  <https://learn.microsoft.com/en-us/windows/wsl/case-sensitivity>
