# Using SkillView

For installation and a quick start, see the [README](../README.md).

## TUI

### TUI layout

The TUI is organized around three primary tabs in a persistent top header — **Discover**, **Installed**, and **Changes** — plus a Doctor view reachable on demand. Discover and Installed pair a list on the left (60% of the width) with a contextual detail pane on the right (40%); Changes uses a full-width table for the maintenance queue. In Discover, the right pane keeps a compact metadata summary at the top and one main body below for description, preview, or logs. The active tab is highlighted in the accent color; the status bar at the bottom advertises the shortcuts available in the current view.

| Tab / view | What it does | How to open |
|---|---|---|
| **Discover** ◇ | Search public skills, refine by owner/agent/limit, preview `SKILL.md`, inspect metadata, flip the right pane between preview and logs, and stage installs. Default landing view. | `1`, click the pill, or `←/→` to cycle |
| **Installed** ▣ | Lists installed skills across the local inventory, filter, cycle sort, cycle a pinned/unpinned filter, inspect details, open the folder, or remove. | `2`, click the pill, or `←/→` |
| **Changes** △ | Maintenance queue showing pending cleanup tasks. Press Enter to open the appropriate specialist view. | `3`, `u`, click the pill, or `←/→` |
| **Doctor** | Full-screen environment report: `gh` path/version, auth state, detected capabilities, installed agent homes, and log location. Esc returns to the previous tab. | `d` |
| **Install — compact** | One-screen confirm: scope radio, agent checkboxes pre-selected from your home directory, **Install** / **Advanced…** / **Cancel**. | `i` from a Discover result |
| **Install — advanced wizard** | Full multi-step dialog with version, scope, agent, path, overwrite, and options for hidden-dir scanning, installing from a republished skill's upstream source, or local installs. | `I` from a Discover result, or **Advanced…** from the compact modal |
| **Remove — compact** | `[y]es / [n]o` confirm for simple single-skill removes. | `x` from an Installed row whose plan is straightforward |
| **Remove wizard** | Multi-step review/confirm for plans with incoming symlinks, validation warnings, or package/repo group removes. | Automatically escalated from `x` when needed |
| **Cleanup view** | Finds duplicates, broken symlinks, residue, and other cleanup candidates; remove or ignore them in batches. | `c` |
| **Help overlay** | Grouped Markdown reference for every keybinding. | `?` or `F1` |

Each tab preserves its own state (filter text, selection, sort, marks) when you switch away and back.

### Find and install a skill

The main "discover and inspect" loop:

1. Type a search query, and optionally press `f` to narrow the next search with **Owner**, **Agent**, **Limit**, and hidden-dir options.
2. Browse results in the left table; selection drives the detail pane on the right.
3. Press `S` to cycle a sort (stars ↓ → name ↑ → name ↓ → repo ↑ → off). The active sort column's header shows the direction.
4. Press `e` to flip the detail pane between rendered markdown and raw, `o` to open the repo in a browser, or `l` to inspect logs.
5. Press `i` to stage an install, or `I` for the advanced wizard.

The same operations are available from the CLI; see [CLI usage](#cli-usage).

### Keyboard reference

Navigation:

| Key | Action |
|---|---|
| `1` / `2` / `3` | Jump directly to Discover / Installed / Changes |
| `←` / `→` | Cycle tabs |
| `↑` / `↓`, `PgUp` / `PgDn`, `Home` / `End` | Move through rows |
| `Tab` / `Shift+Tab` | Move focus between list and detail |
| `/` | Jump to Discover and focus the search box |
| `?` or `F1` | Open the help overlay |
| `Esc` | Leave a field or back out of the current sub-view / modal |
| `q` | Quit from a top-level list or preview |
| `Ctrl+Q` | Quit from anywhere, including while typing |

Discover tab:

| Key | Action |
|---|---|
| `Enter` (or `Ctrl+J` in Warp) | Submit search from the query field, or preview from the results table |
| `p`, `v`, `→` | Preview the selected result |
| `f` | Open the Discover filters dialog (owner, agent, limit, hidden dirs) |
| `S` | Cycle results sort |
| `i` | Compact install for the selected result |
| `I` | Advanced install wizard for the selected result |
| `o` | Open the repo in a browser |
| `e` | Toggle raw / rendered preview |
| `l` | Toggle the right pane between preview and logs |

Installed tab:

| Key | Action |
|---|---|
| `f` | Focus the filter field |
| `s` | Cycle sort (name / package / location) |
| `P` | Cycle pin filter (all / pinned only / unpinned only) |
| `x` | Remove the selected skill (compact confirm; wizard if the plan needs second-confirm) |
| `o` | Open the skill folder |

Changes tab:

| Key | Action |
|---|---|
| `Enter` | Open the selected maintenance item |

Other:

| Key | Action |
|---|---|
| `d` | Open Doctor (full-screen) |
| `c` | Open Cleanup |

**Warp note:** if `Enter` is unreliable after the first interaction, use `Ctrl+J` or `→` for preview.

### Themes and configuration

- `--theme default` uses the SkillView warm palette (gold accent, beige text, dark surfaces, mint/red/blue/purple state colors) on truecolor terminals.
- `--theme high-contrast` switches to a 16-color StandardColor scheme for screen readers, terminals without truecolor, or low-contrast environments.
- `SKILLVIEW_THEME=high-contrast` is the environment-variable equivalent of `--theme high-contrast`.
- Keybindings are intentionally fixed in-app; there is no SkillView keybinding remap file, so the shortcuts documented here are the supported contract.

## CLI usage

SkillView runs in CLI mode when you provide a subcommand.

| Command | What it is for |
|---|---|
| `skillview doctor` | Inspect environment, auth, capabilities, and log paths |
| `skillview list` | Show installed skills from filesystem and, when available, `gh skill list` |
| `skillview rescan` | Re-run inventory capture and print a summary |
| `skillview search <query>` | Search public repositories for skills |
| `skillview preview OWNER/REPO [SKILL]` | Render a skill preview without installing |
| `skillview install OWNER/REPO [SKILL]` | Install a skill with SkillView's wrappers and diff output |
| `skillview update [...]` | Dry-run or apply skill updates |
| `skillview remove <skill>` | Remove an installed skill with safety checks |
| `skillview cleanup` | Report or apply cleanup actions |

Examples:

```bash
skillview list --json
skillview search prompt --owner github
skillview preview github/awesome-copilot documentation-writer
skillview install github/awesome-copilot git-commit --agent claude-code --scope user
skillview update --dry-run
skillview cleanup --apply --yes
```

### Global flags

```bash
skillview --help
skillview --version
gh skillview --help
gh skillview --version
skillview --debug
skillview --theme high-contrast
skillview --scan-root /path/to/skills
skillview --scan-root /path/one --scan-root /path/two list --json
```

- `--help` prints a Markdown usage guide for the active entrypoint (`skillview` or `gh skillview`)
- `--version` prints both the SkillView version and the Terminal.Gui version in use
- `--debug` works before or after the subcommand
- `--theme` accepts `default` or `high-contrast`
- `--scan-root` is repeatable
- `SKILLVIEW_LOG=debug` is also supported

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success or nothing to do |
| `1` | User-level error |
| `2` | Invalid usage |
| `10` | Environment error |
| `20` | No matches |
| `130` | Canceled by the caller or Ctrl+C |

### Automation and AI-agent usage

SkillView's CLI is designed to be automation-friendly when you want higher-level safety than raw `gh skill`.

- Prefer `--json` on commands that support it: `doctor`, `list`, `search`, `preview`, `install`, `update`, `remove`, and `cleanup`.
- Use exit codes as the control surface for scripts: `0` success, `2` invalid usage, `10` environment/setup problems, `20` no matches, `130` canceled.
- Put global flags like `--scan-root` and `--theme` **before** the subcommand; only `--debug` is accepted after the subcommand.
- `skillview doctor --json` is the fastest way for an agent or script to confirm `gh` version, auth state, capability probes, and log location before attempting an install/update flow.

Examples:

```bash
skillview doctor --json
skillview list --json
skillview search prompt --owner github --json
skillview update --all --dry-run --json
skillview cleanup --candidates --json
```

## Troubleshooting

SkillView keeps a rotating file log and redacts sensitive values before writing.

- Linux: `~/.cache/SkillView/logs`
- macOS: `~/Library/Caches/SkillView/logs`
- Windows: `%LOCALAPPDATA%\SkillView\logs`

If the TUI behaves unexpectedly:

1. run with `--debug`
2. open Doctor with `d`
3. check the log file

If you open a bug, include:

1. `skillview --version`
2. your terminal emulator and OS
3. the exact command or TUI flow
4. whether `gh auth status` is healthy
5. the relevant debug log excerpt if you have one

## GitHub CLI compatibility

SkillView requires `gh` 2.97.0 or newer and a terminal with ANSI support. It complements `gh skill`; use the [upstream commands](https://cli.github.com/manual/gh_skill) for options SkillView does not expose, including [`gh skill publish`](https://cli.github.com/manual/gh_skill_publish).

Inventory scans conventional user skill locations as well as `CLAUDE_CONFIG_DIR` for Claude Code and `PI_CODING_AGENT_DIR` for Pi.
