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
  updating UI.
- Installed, Changes, and Updates cancel superseded inventory loads. Pass their
  cancellation token through to inventory capture; do not add generation-only
  refreshes that leave obsolete scans running.
- Async update/install controls stay disabled while their operation is active.
  Continue to pass cancellation into subprocess-backed services.
- `ProcessRunner` retains at most 1 MiB of child output per stdout/stderr stream,
  plus a small truncation marker. Increase this only with evidence that a
  supported command needs more structured output.
- `SearchAgentMetadataCache` is a thread-safe 512-entry LRU. Do not replace it
  with an unbounded dictionary.
- Logger subscriptions are disposable. Every long-lived subscriber must retain
  and dispose its subscription. The visible log pane retains at most 512
  formatted lines and coalesces burst refreshes.

## Focused verification

Run `dotnet build`, then `dotnet test --no-build`. Resource-focused coverage
includes large subprocess output, LRU eviction, subscription disposal,
overlapping preview cancellation, superseded inventory scans, inline busy
state, and layout guards at 80×24, 100×30, and 140×42.
