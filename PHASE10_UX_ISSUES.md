# Phase 10 — UX Issues & Improvements

Compiled from demo video review (skillview_demo1.mov, 2:48, macOS Terminal.app)
and competitor analysis (SkillKit TUI, SkillsGate TUI, SkillDeck desktop).

---

## 1. Replace modal dialogs with full-screen view swapping

**Priority: Critical — architectural change, fixes multiple issues at once**

Every screen (Doctor, Installed, Update, Cleanup, Search+) currently opens as a
`Dialog` at `Dim.Percent(90)`. This causes:

- **Bleed-through**: Main window search results + preview content peek through
  around all edges. Text fragments ("al", "ons", "ity.", "tGUI", "next") from
  the preview and truncated skill names ("over", "awes", "angu") from the left
  table are visible behind every dialog. (Observed in frames 56, 76, 96, 106,
  116, 136, 142, 148 of the demo video.)

- **Stale preview behind dialogs**: The right pane title still shows
  "Preview — DimitriGilbert/ai-skills/awesome-creator" and rendered markdown
  bleeds through behind Doctor/Installed/Update/Cleanup dialogs.

- **Key leakage**: `OnWindowKeyDown` fires during modal dialogs — the main
  window's status line updates ("preview DimitriGilbert/...") while the Search+
  dialog is active. Keys intended for the dialog trigger background actions.

- **Stale status bar**: The main window's StatusBar key hints (Search /, Doctor d,
  etc.) show during modals. The status message persists from previous operations
  (e.g. "cleanup: removed 0, ignored 0" stays after returning to main view).

**Solution**: Switch to a view-swapping architecture. Each screen replaces the
main window's content (left frame + right frame) rather than overlaying a dialog.
Both SkillKit and SkillsGate validate this pattern — neither uses modal overlays
for primary views. SkillsGate uses tab switching with `1/2/3` keys; SkillKit uses
letter-key navigation between full-screen views.

Reserve `Dialog` for small, focused interactions only: confirmation prompts,
file pickers, help text, error details.

**Files affected**: `SkillViewApp.cs`, `InstalledScreen.cs`, `UpdateScreen.cs`,
`CleanupScreen.cs`, `DoctorScreen.cs`, `SearchScreen.cs`, `RemoveScreen.cs`.

---

## 2. Contextual StatusBar / key hints per view

**Priority: High**

Our StatusBar always shows the same 9 shortcuts regardless of context. Both
SkillKit and SkillsGate show different shortcuts per screen. SkillsGate even
renders contextual hints in the metadata panel (e.g. "d Remove skill" when
installed, "i Install skill" when not).

After switching to full-screen views, the StatusBar should update to show only
the keys relevant to the active view. For example:

- **Search view**: `/ Focus query | Enter Preview | i Install | l Logs | q Quit`
- **Installed view**: `/ Filter | Enter Detail | r Remove | Esc Back | q Quit`
- **Update view**: `Space Toggle | d Dry-run | u Update | Esc Back`
- **Doctor view**: `Esc Back | q Quit`

**Files affected**: `SkillViewApp.cs` (StatusBar creation), each screen.

---

## 3. Dry-run produces no visible output

**Priority: High**

In the demo, user selects `ab-test-setup` ✓, presses Dry-run. Status shows
"running dry-run…" but the preview pane stays at "(dry-run results appear here)"
or goes blank.

**Root cause**: `gh skill update --dry-run ab-test-setup` exits 0 with empty
stdout when no updates are available. The code sets `"(no output)"` which is
barely visible. Also, dry-run with NO skills selected silently runs a global
dry-run (line 179 guard only blocks non-dry-run).

**Fix**:
- Block dry-run when no skills are selected (same guard as regular update).
- When dry-run returns empty output, show an explicit message:
  "No updates available for the selected skill(s)."
- Make the dry-run result pane visually distinct (e.g. a Markdown summary).

**Files affected**: `UpdateScreen.cs` (lines 179, 212–215).

---

## 4. Logs view doesn't change the left pane

**Priority: High**

Pressing `l` loads logs into the right pane, but the left pane still shows
search results. Users expect the logs to be primary content, not side-by-side
with unrelated search results. (Observed in frame 156 of the demo.)

**Fix**: When switching to logs, either:
- (a) Take over the full window (hide left pane, logs fill entire width), or
- (b) Replace the left pane content with a log-level filter or log source list.

Option (a) is simpler and matches the full-screen view pattern.

**Files affected**: `SkillViewApp.cs` (`ToggleRightPane` method).

---

## 5. Stale status messages persist across views

**Priority: High**

After closing Cleanup: "cleanup: removed 0, ignored 0" stays on main screen.
After preview loads: "preview loaded" persists forever. No automatic status
reset when returning from a sub-view. (Observed in frames 152, 156.)

**Fix**: Reset the status message to a contextual default when:
- Returning from a sub-view to the main search view.
- The status message is older than N seconds (auto-clear timer).
- The user performs a new action that changes context.

The notification bar pattern from SkillsGate (temporary colored message that
auto-dismisses) is a cleaner approach than our persistent label.

**Files affected**: `SkillViewApp.cs` (`SetStatus`, view transition methods).

---

## 6. TableView columns truncate excessively

**Priority: Medium**

Hard-coded `Truncate(s.SkillName, 24)`, `Truncate(s.Repo, 30)`,
`Truncate(s.Description, 60)` in the main results table. At 150 columns wide
there is plenty of room, but descriptions show "Guide for u…", "Generate Aw…",
"GitHub Copi…". (Observed in frames 16, 36, 152, 156; also noted in
PHASE9_NOTES.md.)

Similarly in InstalledScreen: names truncated to 28 chars with 60% width
allocation on a wide terminal.

**Fix**: Compute truncation limits dynamically based on the available terminal
width. Give the Description column the remaining space after fixed-width columns
(Skill, Repo, ★) are allocated. Consider using T.GUI's column auto-sizing if
available, or calculate proportional widths.

**Files affected**: `SkillViewApp.cs` (`RefreshResultsTable`), `TuiHelpers.cs`,
`InstalledScreen.cs`, `UpdateScreen.cs`, `CleanupScreen.cs`.

---

## 7. Cleanup/Installed detail table column wrapping

**Priority: Medium**

In the Cleanup detail Markdown pane, field names wrap badly: "provenan ce",
"validi ty", "symlin ked", "tree-s ha", "versio n" because the Markdown table
column is too narrow for the content. (Observed in frame 148.)

**Fix**: Use shorter field labels in the detail Markdown tables (e.g. "Source"
instead of "provenance", "Valid" instead of "validity"). Or switch from a
Markdown table to a structured key-value layout:

```
**Source**: github
**Valid**: yes
**Linked**: no
```

This avoids the column width problem entirely and is what SkillsGate does in
its metadata sidebar.

**Files affected**: `InstalledScreen.cs`, `CleanupScreen.cs` (detail rendering
methods).

---

## 8. Warp terminal tip shown on non-Warp terminals

**Priority: Medium**

`WelcomeHint` includes "Tip: In Warp terminal, use Ctrl+J instead of Enter"
even when running in Terminal.app. The text is not gated on `IsWarpTerminal`.
(Observed in frame 16.)

**Fix**: Split the welcome text. Only include the Warp tip when
`TuiHelpers.IsWarpTerminal` is true.

**Files affected**: `TuiHelpers.cs` (`WelcomeHint` constant).

---

## 9. Two search screens with unclear relationship

**Priority: Medium**

Main window `/` search vs. `S` Search+ dialog are confusing. Users don't know
which to use or why they're different. The StatusBar labels them "Search /" and
"Search+ s" with no explanation.

- Main window `/`: single query field, results in the left pane, preview in
  the right pane. No owner filter or limit control.
- Search+ `S`: modal dialog with Query + Owner + Limit fields, its own results
  table and preview, and install staging via `i` key.

**Fix**: Merge into a single search experience. The main view should gain the
Owner and Limit fields (collapsed/togglable if they add clutter). Remove the
separate SearchScreen dialog. The `i` install action can work from the main
results table.

**Files affected**: `SkillViewApp.cs`, `SearchScreen.cs` (may be removed).

---

## 10. Metadata sidebar in skill detail view

**Priority: Medium — borrowed from SkillsGate**

SkillsGate's detail view uses a 70/30 split: rendered SKILL.md on the left,
structured metadata panel on the right showing Name, Description, Source, URL,
Status, Agents, Install/Update dates, and contextual key hints.

Our preview pane shows the skill's SKILL.md content but no structured metadata
card. When previewing a skill from search results, we could display a metadata
panel alongside the rendered content.

**Files affected**: `SkillViewApp.cs` (preview rendering).

---

## 11. Raw/Rendered toggle for skill content

**Priority: Medium — borrowed from SkillsGate**

SkillsGate's `e` key toggles between rendered Markdown and raw SKILL.md source.
We already have the raw text (the preview service returns it). Adding a toggle
between our Markdown view and a read-only TextView is straightforward.

**Files affected**: `SkillViewApp.cs` (preview pane), `TuiHelpers.cs`.

---

## 12. Open folder/URL action

**Priority: Medium — borrowed from SkillsGate**

SkillsGate's `o` key opens the skill's directory in Finder (local skills) or
source URL in browser (remote/GitHub skills). Easy to implement with
`Process.Start` on macOS/Linux/Windows.

Useful for installed skills (open the skill directory) and search results
(open the GitHub repo page).

**Files affected**: New action in `SkillViewApp.cs` or `InstalledScreen.cs`.

---

## 13. Per-agent removal

**Priority: Low — borrowed from SkillsGate**

When removing a skill installed in multiple agents, SkillsGate shows a numbered
menu to pick which agent to remove from (1-9 keys), or `a` for all agents.
Our RemoveScreen removes from everywhere — adding per-agent choice is more
precise.

**Files affected**: `RemoveScreen.cs`.

---

## 14. Notification bar (auto-dismissing)

**Priority: Low — borrowed from SkillsGate**

SkillsGate uses a temporary colored notification bar (success=green, error=red,
info=blue) that auto-dismisses after a few seconds. This is a cleaner pattern
than our persistent status label that goes stale. Could replace or supplement
our `_statusLabel`.

**Files affected**: `SkillViewApp.cs`.

---

## 15. Agent icons/badges

**Priority: Low — borrowed from SkillKit + SkillsGate**

SkillKit uses Unicode symbols per agent: ⟁ Claude, ◫ Cursor, ◎ Codex,
✦ Gemini, ⬡ OpenCode. SkillsGate uses colored two-letter abbreviations.
We show agent names in a comma-separated string — compact badges would be more
scannable in the Installed table.

**Files affected**: `InstalledScreen.cs`, `TuiHelpers.cs`.

---

## 16. Quality grades (A-F)

**Priority: Low — borrowed from SkillKit**

SkillKit assigns A-F quality grades with color coding (A=green, B=green,
C=yellow, D=red, F=red) based on SKILL.md analysis (completeness, structure,
documentation quality). Would add a discovery signal to search results and
installed skills.

**Files affected**: New quality analysis module, table column additions.

---

## 17. Live search filtering for Installed view

**Priority: Low — borrowed from SkillKit + SkillsGate**

Both competitors support filtering the installed skills list in-place as you
type, rather than running a new API search. Our Installed view loads all skills
at once — adding a client-side filter field would make it easy to find skills
in large collections (the demo showed 86 installed skills).

**Files affected**: `InstalledScreen.cs`.

---

## Implementation Order

1. **Item 1** — Full-screen view swapping (architectural, fixes items 1 + parts of 2, 4, 5)
2. **Item 2** — Contextual StatusBar
3. **Item 3** — Dry-run fix
4. **Item 9** — Merge search screens
5. **Item 6** — Dynamic column widths
6. **Item 7** — Detail table formatting
7. **Item 8** — Warp tip gating
8. **Item 5** — Status message lifecycle
9. **Items 10-17** — Incremental enhancements
