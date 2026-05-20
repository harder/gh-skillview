# SkillView workflow-first UX design

Date: 2026-05-20

## Goal

Redesign SkillView's TUI so it feels as clean and legible as `winget-gui-tui`, while staying optimized for AI skill management instead of package management.

The result should:

- keep the shell calm, consistent, and keyboard-first,
- make install location, agent membership, provenance, and health obvious,
- reduce modal churn by keeping more work in one persistent shell, and
- use current Terminal.Gui 2.2.x patterns instead of older framework workarounds where newer APIs already solve the problem.

## Design inputs

This design is based on three sources:

1. SkillView's current shell and workflow split in `SkillViewApp`, `InstalledTabView`, `UpdatesTabView`, and `SkillDetailPaneView`.
2. `winget-gui-tui`'s proven shell patterns: pill tabs, stable list/detail split, custom status strip, strong focus styling, and a persistent detail workspace.
3. Current Terminal.Gui 2.2.x usage patterns already validated in both repos: `Application.Create().Init()`, `HasFocusChanged`, `CollectionNavigator = null`, paste events on text inputs, and custom views where the stock controls still do not provide the right UX primitive.

## Non-goals

- Do not turn SkillView into a visual clone of `winget-gui-tui`.
- Do not flatten SkillView's agent- and location-aware workflows into package-style one-row-per-agent views.
- Do not remove advanced install and maintenance scenarios such as shared installs, symlinked installs, package-backed installs, scan-root diagnostics, or capability-gated actions.

## Top-level model

Replace the current top-level shell split with three primary workspaces:

1. **Discover** — search, inspect, and install skills from remote or local sources.
2. **Installed** — browse the current inventory of physical installs and manage them in place.
3. **Changes** — triage updates, cleanup work, and diagnostics in one maintenance-focused workspace.

`Doctor` stops being a peer top-level tab. Its information moves into **Changes** and into inline health badges throughout the shell. This keeps the information architecture as compact as `winget-gui-tui` without hiding the extra complexity SkillView needs.

## Shell layout

The shell keeps the strongest structural cues from `winget-gui-tui`:

- a pill-style tab strip at the top,
- a one-line context bar below it,
- a stable primary/detail split near 60/40,
- heavy borders on the focused pane and lighter borders on the unfocused pane, and
- one bottom status strip that shows active facets on the left and the most relevant shortcuts on the right.

The visible shell wording should prefer **Locations** or **Install locations** over **roots**. Keep **roots** for doctor-grade diagnostics only.

## Workspace responsibilities

### Discover

Discover is the browsing and install workspace. It keeps the left-side results list and right-side detail pane, but the facets are SkillView-specific:

- agent,
- owner,
- provenance or source,
- hidden-directory allowance,
- install target or location, and
- health or compatibility cues when available.

Search state stays warm. The user can preview a skill, install it, return to the list, and continue exploring without losing query context or selection.

Install flows should move away from full-screen modal churn. Use compact confirmation or input surfaces only where the user must choose a version, path, or capability-gated flag.

### Installed

Installed becomes the inventory truth view. Rows represent the physical install identified by `InstalledSkill.ResolvedPath`, not a single agent projection.

Each row should make these dimensions visible without opening a modal:

- skill name,
- agent membership,
- location,
- provenance or package source,
- symlink state,
- pinned state, and
- health or validity.

The right pane becomes the action anchor for the selected install. It should show metadata, rendered or raw preview, logs on demand, and context-sensitive actions such as reveal path, open upstream, remove or unlink, cleanup, and jump into related maintenance work.

### Changes

Changes is the maintenance workspace. It groups the tasks that answer the same question: what needs attention now?

It contains three related queues:

1. updates,
2. cleanup candidates, and
3. diagnostics or scan issues.

This keeps the top-level shell small while still giving SkillView space to explain shared installs, symlink cleanup, unsupported flags, package-backed lockfile state, and scan-root problems.

## Interaction model

The redesign should follow these interaction rules:

- Keep the shell keyboard-first, but make the current focus unmistakable.
- Preserve selection and cursor anchor after refresh or rescan.
- Disable `TableView` type-to-search where it conflicts with view-level shortcuts by setting `CollectionNavigator = null`.
- Use one-line facet chips and status summaries instead of burying state in prose labels.
- Keep the right pane persistent across mode switches where practical so actions stay visually anchored to the selected item.
- Reserve full modal workflows for confirmation and narrow inputs, not as the default working surface.

## Component plan

### Header and navigation

Keep the custom tab bar approach. Terminal.Gui's stock tab model is still focus-driven, and SkillView needs logical tabs whose active state is independent of which child control currently has focus.

### Context bar

Add a one-line context bar under the header. It should surface the active agent, location, provenance, pin, and health facets, plus quick filter state. This is the main replacement for scattered labels and per-tab state text.

### Primary tables

Keep `TableView`, but make the UX more deliberate:

- stable column widths,
- stronger selected-row styling,
- row badges or compact columns for agent membership and health,
- predictable shortcut routing, and
- refresh behavior that preserves position where possible.

### Detail workspace

Evolve `SkillDetailPaneView` into a true detail workspace with:

- metadata chips at the top,
- rendered, raw, and log subviews,
- context-aware action chips, and
- clearer grouping between metadata, content preview, and operation feedback.

### Status strip

Adopt a custom status-strip model closer to `winget-gui-tui` than Terminal.Gui's stock `StatusBar`. The bottom row should be informational first: left-side facet badges, center status, right-side shortcut hints, with graceful truncation when space is tight.

## State and data flow

Keep one authoritative shell state with generation-guarded async updates. The top-level state should track:

- active workspace,
- active facet set,
- current selection,
- current detail payload,
- latest inventory snapshot, and
- current maintenance queue state.

Each workspace can keep small local state, such as Discover's query text or Changes' active maintenance section, but inventory and selection truth should stay centralized.

Operationally, actions should follow one loop:

1. capture or refresh inventory,
2. derive visible rows from the snapshot and active facets,
3. load richer detail for the selected item,
4. execute the selected action, and
5. rescan and restore context in the same shell.

## Error handling and feedback

Errors should stop feeling like detached log blobs.

Capability gaps, invalid installs, broken symlinks, unsupported flags, and path-safety failures should surface in three layers:

1. inline health badges on the affected row,
2. right-pane explanation and next-step text for the selected item, and
3. status-strip summary for the current operation.

Logs remain available, but as a drill-down view, not the primary place the user learns that something went wrong.

## Terminal.Gui guidance

Use the current Terminal.Gui 2.2.x path already validated in this repo and in `winget-gui-tui`:

- keep the instance-based `Application.Create().Init()` lifecycle,
- use `HasFocusChanged` to drive focused-pane emphasis,
- use text-input paste events where they improve search and filter ergonomics,
- keep custom views for the tab strip, status strip, and rich detail presentation where Terminal.Gui still lacks the right primitive, and
- avoid reviving workarounds for older `TableView` behavior that 2.2.x already replaced.

## Testing

Treat the shell behavior as a UX contract and test it accordingly.

Add or expand coverage around:

- top-level workspace switching,
- context-bar and status-strip content,
- focused vs unfocused border styling,
- cursor restoration after refresh,
- action availability under capability gates,
- health badge and detail rendering for invalid or shared installs,
- changes-queue behavior for updates, cleanup, and diagnostics, and
- the physical-install inventory model where one install can belong to multiple agents.

For the custom Terminal.Gui views, keep small targeted tests that protect the specific surfaces this redesign depends on, the same way `winget-gui-tui` guards its custom shell widgets against Terminal.Gui upgrades.

## Recommended rollout

Implement the redesign in this order:

1. shell chrome and shared component upgrades,
2. Discover workspace reshape,
3. Installed workspace reshape,
4. Changes workspace introduction and Doctor absorption, and
5. focused tests for the new UX contract.

This keeps the app usable throughout the redesign and lets the new shell land before the deeper workflow migration.
