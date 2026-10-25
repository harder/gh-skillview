# Using SkillView

For installation and a quick start, see the [README](../README.md).

## TUI

### TUI layout

The TUI is organized around three primary tabs in a persistent top header — **Discover**, **Installed**, and **Changes** — plus a Doctor view reachable on demand. Discover and Installed pair a list on the left (60% of the width) with a contextual detail pane on the right (40%); Changes pairs its maintenance queue with frozen observation details. In Discover, the right pane keeps a compact metadata summary at the top and one main body below for description, preview, or logs. The active tab is highlighted in the accent color; the status bar at the bottom advertises the shortcuts available in the current view.

| Tab / view | What it does | How to open |
|---|---|---|
| **Discover** ◇ | Search public skills, refine by owner/agent/limit, preview `SKILL.md`, inspect metadata, flip the right pane between preview and logs, and stage installs. Opens when no skills are installed. | `1`, click the pill, or `←/→` to cycle |
| **Installed** ▣ | Lists installed skills across the local inventory, filter, cycle sort, cycle a pinned/unpinned filter, inspect details, open the folder, or remove. | `2`, click the pill, or `←/→` |
| **Changes** △ | Maintenance observations grouped by issue and physical installation, including cleanup review and unchecked installations. Enter opens the exact selected target. | `3`, `u`, click the pill, or `←/→` |
| **Doctor** | Full-screen environment report: `gh` path/version, auth state, `gh skill` availability, installed agent homes, and log location. Esc returns to the previous tab. | `d` |
| **Install — compact** | One-screen confirm: scope radio, agent checkboxes pre-selected from your home directory, **Install** / **Advanced…** / **Cancel**. | `i` from a Discover result |
| **Install — advanced wizard** | Full multi-step dialog with version, scope, agent, path, overwrite, and options for hidden-dir scanning, installing from a republished skill's upstream source, or local installs. | `I` from a Discover result, or **Advanced…** from the compact modal |
| **Remove — compact** | `[y]es / [n]o` confirm for simple single-skill removes. | `x` from an Installed row whose plan is straightforward |
| **Remove wizard** | Multi-step review/confirm for plans with incoming symlinks, validation warnings, or package/repo group removes. | Automatically escalated from `x` when needed |
| **Cleanup view** | Finds name collisions, metadata issues, broken symlinks, and residue; compare/review uncertain items and remove eligible cleanup candidates. | `c` |
| **Help overlay** | Grouped Markdown reference for every keybinding. | `?` or `F1` |

Each tab preserves its own state (filter text, selection, sort, marks) when you switch away and back.

Startup checks the local inventory. If skills are found before you start
interacting, it opens Installed and focuses the list. Otherwise it keeps the
current view. From a list, press `/` to focus the Discover search box.

### Find and install a skill

The main "discover and inspect" loop:

1. Focus the Discover search box (press `/` from a list), then type a search query. Leave the field and press `f` to narrow the next search with **Owner**, **Agent**, **Limit**, and hidden-dir options.
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
| `R` | Review replacement from the selected installation’s same recorded local source; reopen saved history by ID |
| `P` | Cycle pin filter (all / pinned only / unpinned only) |
| `K` | Check selected local package metadata and supported references |
| `x` | Remove the selected skill (compact confirm; wizard if the plan needs second-confirm) |
| `o` | Open the skill folder |

Press **F2** in Installed to show or hide technical evidence (installation IDs, captured identities, exposure paths, and reload references). The default detail pane starts with the installation/package summary. The description follows the observed evidence and is labeled as skill metadata.

Installed columns are **Name → State → Loc → Agents → Package**. Location and known single-agent badges use subtle color cues in the default theme; their text remains visible in every theme. Mixed-agent badges retain the normal text color. The Package column appears when the inventory includes packages. Headers stay visible while scrolling.

Installed and Changes summaries shorten paths beneath the known home directory used for discovery to `~` (for example, `~/.agents/skills/demo`). F2 technical evidence keeps full paths for copying and inspection. Opening folders and maintenance actions always use the original paths.

Inventory loads and Discover searches show a centered spinner while replacing old rows and details. Leaving a tab clears its loading indicator. Installed and Changes share a complete inventory snapshot for up to one minute; partial captures retry on the next visit; switching tabs during a scan joins that same background capture. Press **r** from a list to read disk again immediately. Maintenance actions validate current evidence before acting. Loading keeps browse focus out of text fields and temporarily disables actions on hidden selections. Help, quit, tab navigation and refresh remain available; intentional query/filter editing keeps its focus.

Changes tab:

| Key | Action |
|---|---|
| `Enter` | Review the selected maintenance item without changing files |
| `F2` | Switch between its issue summary and technical details |
| `r` | Refresh disk inventory; upstream availability and running-session state remain unknown |

Each observation retains its installation address ID, exact affected paths,
source/ref/tree, recorded owners and consumers. Cleanup issues reopen only that
issue at that physical address after a fresh inventory capture. Installation
review opens Updates with only that exact installation selected, clearing prior
batch and `--all` choices. A missing or ambiguous ID refuses the hand-off instead
of choosing a same-name sibling. Press **Check updates** explicitly to check update
availability; **c** compares installed content with one immutable saved candidate
and opens its plan review (**u** also prepares review). Entry into the
workspace performs neither action. Refresh keeps an exact Updates selection.
The TUI caps visible rows and selected-detail text, reports omitted row/field
counts, and omits exact fields whole. `queue --json` makes a fresh capture with
full evidence; keep the same global workspace/profile/scan-root options.

For **Same name, multiple copies** or **Duplicate** observations, Enter opens
**Copies and versions**. Each row is one physical installation; its recorded
consumers share those files. Select a row to see its full location, source,
recorded ref, authored version and pin. Related rows include matching names and
matching recorded sources/subpaths; matching names alone do not prove a common
source. Unknown provenance and incomplete discovery stay visible.

The original selection is the reference. Select another row and press
**Compare** to inspect local file differences, or **Use as reference** to change
the pair. This does not check upstream availability or infer which version is
best. **Update / version...** selects that exact location in the existing Updates
workspace, where **Choose version...** offers a Git tag. **Cleanup review...**
opens the matching captured issue at that location; name conflicts remain
review-only. Actions without an applicable finding, and updates to managed
read-only copies, are disabled with an explanation in the details.
Opening either review changes no skill files. The list shows at most 128 copies;
comparison summaries show at most 12 changed paths and report omissions.

In Updates, the selected-copy and saved-review summaries use the full reading
width. **Review update** prepares one immutable candidate; it does not apply it.
The summary explains the target, revision, affected consumers, local changes and
retained original. Press **F2** or **Details (F2)** for the existing fingerprints,
frozen source diffs, complete diagnostics and recovery evidence; F2 returns to
the summary. Switching views changes neither the candidate nor its approvals.
In the general Updates list, an availability check returns focus to the control
that started it, so table navigation and marking can continue. The button
mnemonics are **Alt+C** for Check updates and **Alt+R** for Review update.

Review controls appear when applicable. Source review and retention remain
separate declarations: inspect the frozen source changes and omissions in Details,
then check the source declaration and retention approval before applying. Unpin
reviews change only the verified pin. Local-change review remains available
through **Preserve installed**, **Review local** and **Build merge** as applicable;
building a merge requires fresh approval of its exact result. **Next / skip**
continues a batch one installation at a time, preserving earlier outcomes and
diagnostics in Details. A completed disk operation does not prove that an agent
reloaded the skill.

After an attempted write, the result and Details stay available. Press
**Refresh inventory** before starting another review or choosing another version;
this captures the installed ref again at the selected location. A failed refresh
can be retried without granting another approval or selecting a same-name copy.
This also applies when you skip the final batch item after an earlier write.

After a successful revision update, **Undo this update...** opens a fresh
inspection of that saved update. Review the exact location, affected consumers
and original version to restore; F2 shows the inspection fingerprint and saved
evidence. Check the separate restoration approval, then press **Undo update**.
Cancel changes no installed files. Undo refuses if the installed payload, native
objects or required owner evidence changed after the inspection.

Undo restores the retained original, including its earlier local edits and
extras, for every consumer sharing that physical installation. Refresh inventory
afterward to inspect the restored metadata; agent reload remains unverified.
This shortcut belongs to the current successful update result and ends when you
refresh, leave, start another review or attempt undo. Saved inspection/recovery
commands remain available separately. Failed/partial updates and standalone unpin
use their existing explicit recovery paths.

Earlier batch failures remain in Details and do not hide Undo for a later clean
update. If undo restores the original but inventory verification is incomplete,
the result says **Original restored; verification incomplete**. Refresh inventory
to resolve that uncertainty before considering another operation.

Cleanup suppression, agent disablement, pinning and migration remain separate:
suppressed installations still appear for inspection, pins require explicit unpin
review, and disablement/migration are not assessed by this queue. Disk inventory
cannot establish what a running agent has loaded. The queue does not launch
agents. Reload guidance is conditional on the observed agent and delivery type.
For Claude plugin deliveries, background updates change disk while an existing
session retains its loaded version: use `/reload-plugins` or a new session,
following the [official plugin guidance](https://code.claude.com/docs/en/discover-plugins#keep-plugins-updated).
For newly installed Codex skills, the [official catalog](https://github.com/openai/skills#installing-a-skill)
instructs restarting Codex. Other combinations explain that their reload rule is
unknown. Successful disk verification never claims that a running session reloaded.

The CLI exposes the same saved replacement workflows through local plan references:

```bash
skillview --scan-root /path/to/skills inspect --install-id INSTALLATION_ID --json
skillview --scan-root /path/to/skills check --install-id INSTALLATION_ID --json
skillview --scan-root /path/to/skills plan --purpose update-revision --install-id INSTALLATION_ID --json > review.json
skillview --scan-root /path/to/skills inspect --plan-file review.json --json
# Review the comparison, all consumers, source and retention disposition first.
# Supply the reviewed planFingerprint independently; the file is not approval.
skillview --scan-root /path/to/skills apply --plan-file review.json --expect REVIEWED_PLAN_FINGERPRINT --yes --disposition retain-original --json
skillview --scan-root /path/to/skills verify --plan-file review.json --json
# Undo uses the fresh inspection's reviewFingerprint, not the plan fingerprint.
skillview --scan-root /path/to/skills undo --plan-file review.json --expect CURRENT_REVIEW_FINGERPRINT --yes --json
```

Keep HOME, state, CWD and global scan/workspace/profile options unchanged. The
schema-1 `planReference` names an exact plan already retained locally, its purpose,
fingerprints and installation context. It imports no files and cannot authorize a
write. The file must be a bounded regular JSON file (256 KiB maximum); large plan
responses can use only their `planReference` object. After a merge produces a new
plan fingerprint, export a fresh reference with the specialist `update-inspect`.
`plan` also accepts `restore-recorded`, `unpin-only` and `local-reimport` purposes;
apply requires their existing explicit retention disposition. `repair --plan-file`
resumes a recognized interrupted transaction after current inspection; it refuses
to start a ready replacement. Torn owner-image repairs and cleanup remain explicit
specialist operations. Legacy quarantine `undo --id` continues to use its own
review contract. JSON retains exact per-path before/after file evidence, operation
status/reason, affected/retained consumers and unknown session state.

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
| `skillview doctor` | Inspect environment, auth, `gh skill` availability, and log paths |
| `skillview list` | Show installed skills from filesystem and, when available, `gh skill list` |
| `skillview queue --json` | Refresh maintenance observations with schema 1 JSON and exact physical installation IDs; no upstream check or mutation |
| `skillview effective --agent <id> --project <directory>` | Explain conditional loading policy for an agent, profile and project, retaining every observed copy |
| `skillview compare --path <left> --other-path <right>` | Compare two captured installations, including instructions, scripts, resources, permissions and contextual source records |
| `skillview backup --path <installed-directory>` | Capture private, bounded payload evidence with a durable creating/captured journal |
| `skillview rescan` | Re-run inventory capture and print a summary |
| `skillview search <query>` | Search public repositories for skills |
| `skillview preview OWNER/REPO [SKILL]` | Render a skill preview without installing |
| `skillview install OWNER/REPO [SKILL]` | Install a skill with SkillView's wrappers and diff output |
| `skillview update [...]` | Check availability or prepare a saved update review |
| `skillview remove <skill>` | Remove an installed skill with safety checks |
| `skillview cleanup` | Report or apply cleanup actions |
| `skillview ignore --path PATH` | Suppress cleanup suggestions for one physical installation |

Examples:

```bash
skillview list --json
skillview effective --agent gemini-cli --project /path/to/project --client-version 0.46.0 --json
skillview search prompt --owner github
skillview preview github/awesome-copilot documentation-writer
skillview preview cli/cli gh --version v2.103.0
skillview install github/awesome-copilot git-commit --agent claude-code --scope user
skillview update --dry-run
skillview cleanup --apply --yes
```

For `preview`, `--version <ref>` requires a skill name or path. SkillView passes
that revision on the skill selector, as required by `gh skill preview`.

For `install`, use `--agent <id>` to choose an agent and `--scope project|user`
to choose the destination scope. `--path <dir>` selects a custom directory and overrides scope;
`--all` installs every skill found in a repository. To install a specific ref,
pass `--version <ref>` or use `OWNER/REPO@<ref>` as shorthand. Add `--pin` only
when a ref is supplied; for an entire repository, `OWNER/REPO@<ref> --all --pin`
pins that ref. `--upstream` is a switch for a republished skill's declared
upstream source, not a URL argument. Run `skillview --help` for the
complete option list.

`effective` analyzes bounded local configuration and skill metadata without
changing skills or agent configuration. In Installed, select a skill and press
**E** to choose the same context and inspect relevant competing copies.
Use `--cwd`, `--config-dir`, `--profile`, `--client-version`, and, for OpenCode,
`--agent-name` to describe the context precisely. Here `--profile` is a loading
context option; the global inventory `--profile agent[:name]=skills-directory`
still belongs before the subcommand. `--trust trusted|untrusted|unknown` is an
analysis assumption; it never grants trust to an agent.

The JSON schema is version 1. It separates a conditional local-file policy
preview, native inventory observations, and active-session state. A symlink,
cache entry, portable metadata warning, or client version label alone does not
prove native loading. The cited Gemini 0.46.0 source supports an expected rule
profile; native AI-client conformance remains untested. Other rule profiles are
dated documentation previews with explicit version uncertainty. Native providers
currently report unavailable with their startup-audit reason; SkillView does
not start an AI client or execute skill content to obtain a listing.

For `backup`, select one exact installation with `--path` or `--install-id` from
`list --json`. Put global flags before the command, for example:

```bash
skillview --scan-root /path/to/skills backup --path /path/to/skills/demo --json
```

The archive contains file bytes, empty directories, entry Unix modes and native
link bytes. Link targets are not followed. Capture leaves installed payloads
unchanged, including damaged `SKILL.md` files. This first archive format does not
restore ACLs, extended attributes, root permissions or package-owner registries.
For retained-directory replacement and undo, use the separate
[recorded restoration](recorded-restoration.md) or [saved update](saved-updates.md)
workflow.

State defaults to `~/Library/Application Support/SkillView/maintenance` on macOS,
`$XDG_STATE_HOME/SkillView/maintenance` (or `~/.local/state/SkillView/maintenance`)
on Linux, and `%LOCALAPPDATA%/SkillView/maintenance` on Windows.
`SKILLVIEW_STATE_HOME` overrides the base directory. State must be private and
outside installed payloads and recognized agent skill roots, including expected
paths that do not exist yet. Omitted or unresolved workspace/root configuration
refuses capture. An existing shared
state directory is refused rather than having its permissions rewritten. macOS
inherited extended ACLs are also refused; choose a private destination.

Captures retain a creating record before archive work, then publish a captured
record only after source verification and file/directory durability barriers.
Failed or canceled captures retain incomplete evidence. Quotas bound each archive,
total retained bytes and entry counts; reaching a quota refuses new captures and
does not prune history. The lock coordinates SkillView writers. It does not make
an atomic transaction with other installers or a hostile process running as the
same user, and storage hardware still determines power-loss guarantees.

For `update`, select an exact installation with `--path <installed-directory>`
or `--install-id <id>` from `list --json`. A name shared by several installations
requires one of these selectors. `--all` selects the captured inventory; in the
Updates tab, applying marked rows still applies only those rows. `--agent` and
`--scope` are install options and are rejected for updates.

A local install can be reimported from its same recorded absolute source path:

```bash
skillview --scan-root /path/to/skills reimport-plan --path /path/to/skills/demo --json
skillview reimport-inspect --id <saved-id> --json
skillview reimport-apply --id <saved-id> --yes --disposition replace-local-retain-original --expect <plan-fingerprint>
```

This replaces installed content, including extras and source deletions, without
an automatic merge or a recorded base. Review the captured installed/source
archives, metadata policy, changed paths, exact fingerprint and every affected
consumer. The exact original directory remains retained for undo. Supported
source descendant metadata is preserved with installed root privacy; new active
objects have new IDs and timestamps. Owner-managed, pinned, mixed tracking,
linked final source directories or payload entries, overlapping sources, and
incomplete evidence are refused. Ancestor aliases must retain their captured
lexical/canonical mapping. Packages containing nested `SKILL.md` files are
currently refused before staging; nested-skill adapter support remains pending.
Tracking also requires supported scalar metadata in a block mapping; nested or
sequence metadata and ambiguous tracking syntax are refused.

In Installed, select the physical row and press **R**. Choose **Prepare**, review,
check retention approval, then **Apply reviewed**. Enter an existing saved ID and
**Inspect saved ID** to reopen retained history. Recovery/undo each need fresh
inspection and explicit retention approval. The CLI equivalents are
`reimport-recover` and `reimport-undo`, with `--id --yes --expect
<current-review-fingerprint>`. Source freshness is required before the first
retain move. After that move, recovery and undo use frozen evidence even if the
source changes or disappears. When observed, source status is reported separately in JSON's
`operation.localSource` (or `unchanged.localSource` for a verified no-op);
`operation.localImport` records the local source and gh staging witness.
This is a partial local-reimport workflow; other maintenance obligations remain.

Updates preserve exact pins unless `--unpin` is explicit. Ordinary `update`
prepares one immutable candidate for review and reports other selected targets as
not started. It compares the complete payload with its recorded upstream tree,
including scripts, extras, links and Unix permissions. `--force` never approves
replacement. Apply the saved plan with explicit `retain-original` approval; local
edits and extras stay in the retained original and are not automatically merged.
The TUI queues a separate review and approval for each target. See
[saved updates](saved-updates.md) for commands, recovery and undo.

To remove a verified pin without fetching or updating the source, select the
physical installation and save a separate pin-only review:

```sh
skillview --scan-root /path/to/skills unpin-plan --path /path/to/skills/demo --json
skillview unpin-inspect --id <saved-id> --json
skillview unpin-apply --id <saved-id> --yes --disposition unpin-only --expect <plan-fingerprint>
```

Use `--install-id` instead of `--path` when preferred. Keep the same workspace,
home, scan roots and hidden-directory options for subsequent commands. The plan
removes supported `metadata.github-pinned` tracking and, for a verified shared gh
owner, its selected `pinnedRef`. It preserves the recorded source/ref/tree and
installed instruction bytes, extras and supported native metadata. Unsupported
pin syntax, ambiguous ownership and incomplete coverage are refused. A verified
already-unpinned installation produces no plan and consumes no retained-plan
quota.

In Updates, select a row and choose **Plan unpin only** (`p`). Review the exact
saved fingerprint and every affected consumer, check retention approval, then
choose **Apply unpin only**. The existing **unpin** checkbox belongs to updating
to the latest source and is separate from this operation. Every exposure of the
selected physical installation shares the pin change; this action does not
detach one consumer. Publication swaps in a verified directory, so active object
IDs and timestamps change. The exact original native directory remains retained
for undo. After `unpin-inspect`, use `unpin-recover`, `unpin-undo` or
`unpin-repair-owner` with `--yes --expect <current-review-fingerprint>` as needed.
Running agent session state remains unknown.

Update JSON schema 2 retains gh output streams and per-installation outcomes and
adds `savedPlan` plus exact `applyArguments` when a candidate is prepared.
An already-current installation reports `unchanged`, no saved plan and zero
changed files; its installed objects and owner bytes remain untouched. The
Updates queue retains earlier output and outcomes if a later target fails.
Preparation is distinct from installed mutation. A disk change does not prove
that an already-running agent session has reloaded the skill.

Use `compare --path <left> --other-path <right> --json` to inspect two exact copies.
JSON also reports native object/generation fingerprints and observed root Unix
modes separately from file-content fingerprints. Separate copies can have equal
bytes and different native fingerprints. These observations do not attest full
ACLs, extended attributes or Windows alternate streams; recovery needs further
metadata validation. Different observed root modes prevent a consolidation
candidate even when the file bytes match.
Pass their `--scan-root` flags before the subcommand when needed. Comparison stays
offline and reports source, revision, context and whole-payload differences
separately. Unknown evidence and changed owner records require a refresh; identical
bytes alone never authorize removing an exposure or changing ownership.

Shared gh/Vercel, XDG and project lock records are retained in their own contexts.
SkillView protects unsupported schemas, extensions and conflicting owners from
implicit rewrites. Existing shared selectors require update or coordinated
maintenance before reinstalling, including with `--force`. Project/custom updates
preserve the user's shared registry. Owner-aware removal and record repairs remain
refusals until a coordinated maintenance plan is available.

### Static package checks

Use `skillview --scan-root /path/to/skills check --path /path/to/skills/demo --json`
to check one observed installation. `--install-id ID` also selects a physical
installation; an observed agent exposure path may be used with `--path`.
In Installed, select a row and press **K** to open the same read-only check.

JSON schema 2 and the dialog show four independent assessments: portable metadata
format, agent compatibility, payload integrity,
and source review. A valid header can coexist with a broken asset or incompatible
agent framing. Source review remains `not-reviewed`: viewing this report does not
acknowledge source changes or establish authenticity or behavioral safety. A complete
capture and matching local references leave integrity **unknown** because this
command has no trusted or explicitly declared expected payload baseline. Its
separate `observedCondition` records local defects, partial coverage or no defects
within the checked reference scope. The
legacy `agentCompatibility` and `sourceReview` strings retain their schema-1
`not-assessed` meaning; the new `assessments` object carries scoped evidence,
fingerprints, cited rules and unavailable reasons. Saved update fingerprints are
unchanged. `rootMetadataSha256` is a separate raw-file hash.

Findings include source paths, available line numbers, targets and repair guidance.
Read `referenceScope` for the limited Markdown/code-path grammar. Internal fragments
support plain ASCII ATX headings and duplicate heading suffixes; rich headings,
HTML IDs, setext headings and other renderer-specific IDs remain unknown instead
of producing definite missing-anchor findings. Captured links are never followed.

Literal declarations are separate observations. Supported `package.json` dependency
mappings and plain `requirements.txt` names, extras and version constraints report
dependency availability as **unknown**. Missing package-local `file:`/`link:` targets
and `-r` includes have concrete file-repair guidance.
Home-relative `file:~/...` declarations remain unknown; HOME is never expanded and
external dependency paths are never probed. Omitted, unreadable or linked Markdown
also makes declaration coverage partial because Markdown supplies literal evidence.
No installed package manager,
interpreter, credentials or dependency environment is probed. Unsupported manifests,
requirement options/markers/URLs, nonregular inputs and exhausted bounds retain
partial coverage. `declarationScope` states the supported grammar.

Portability observations cover case collisions, Windows path restrictions,
environment-specific references and selected scripts' shebangs/CRLF endings. A URL
observation means literal captured text, including examples; it does not establish
network behavior. Authored tool declarations do not establish effective permissions.
Skill code is never executed, and the check performs no remote lookup or repair.

Optionally select a package rule target, for example:
`skillview --scan-root /path/to/skills check --path /path/to/skills/demo --agent gemini-cli --agent-version 0.46.0 --json`.
This reuses existing version-cited loading rules without launching a client. Known
Gemini 0.46.0 framing rejection can report `incompatible`; passing that limited rule
leaves whole-agent compatibility `unknown`. Unmodeled agents or versions never
receive assumed compatibility. The dialog leaves agent/version unspecified.

Exit 0 means the limited format/reference/declaration checks completed without
errors or partial coverage; it does not turn unknown dependency availability,
unknown agent compatibility or unreviewed source into passed assessments. Findings,
partial coverage or an observed agent incompatibility return 1. Invalid selectors return 2; no unique inventory
match returns 20. Pass global scan roots before `check` and use
`--allow-hidden-dirs` when the desired inventory includes hidden containers.

Saved updates provide [frozen source review](saved-updates.md#frozen-source-review).
Separate workflows support [opt-in isolated evaluations](isolated-evaluations.md).
Source authors can use `gh skill publish --dry-run` directly in their source
repository. `publish --fix` is never a repair action for installed payloads;
no installed authored bytes are rewritten.

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
- `skillview doctor --json` reports the `gh` version, auth state, `gh skill` availability, and log location before an install or update flow.

Examples:

```bash
skillview doctor --json
skillview list --json
skillview search prompt --owner github --json
skillview update --all --dry-run --json
skillview cleanup --candidates=EmptyDirectory --json
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

### Inventory reconciliation

`list --json` reports schema version 3, provider outcomes, partial scans,
inaccessible paths, exact installation IDs, and each agent exposure’s scope.
Authored `version` is separate from `repositoryRef`; source path, pinned ref,
local source and metadata diagnostics are retained. Source identity, physical
installation, contextual owner records and revision/hash domains have separate
fields; agent profile/version/enabled state stays unknown when not observed.

Installation IDs identify addresses. Replacing a directory at the same address
retains that ID; the separately captured native object ID and generation can
change. Inventory observations do not authorize mutation: execution validates
the native object again. Distributor IDs describe observed package labels and
providers; delivery IDs retain version, location and registration context. An
observed marketplace label is not a verified upstream origin.

Use `remove --path <path>` or `remove --install-id <id>` to select one of several
same-name copies. `--agent` accepts the same aliases as inventory filters. Update,
remove and cleanup results include a frozen `impact` with the selected address,
native evidence, owner claims and structured affected exposures. Link-only removal
enumerates every known profile/context sharing that link entry while retaining
the payload and other links. Coverage describes the captured configured roots;
unscanned contexts and active sessions remain unknown. Refusal and cancellation
retain the pre-attempt facts. Historical plans report only what they recorded.

Combined filters such as `list --scope user --agent codex` require a matching
user Codex exposure. A project exposure for another agent cannot supply either
half of that match. Matching rows still show every consumer of the shared
installation. Legacy observations without their own scope use the installation's
declared scope.

A same-name collision is not proof of duplicate content. Collisions, malformed
metadata, conflicting observations and skills absent from a covered gh catalog
are review-only items; `cleanup --apply --yes` cannot delete them. Explicit
removal of an unowned chosen install remains available through the Installed workflow.
An incomplete scan is marked in the Installed footer and Changes summary.

Cleanup reports a verified empty immediate child of a scan root as
`EmptyDirectory`, even when inventory retained it as `MissingSkillMd`.
Non-empty directories with missing or invalid skill metadata remain `Malformed`
review items. This also changes the CLI JSON `kind` and candidate filtering:
`--candidates=Malformed` no longer includes those empty-directory observations.
Both `--candidates=EmptyDirectory` and `--candidates EmptyDirectory` are accepted;
comma-separated kind names are matched without regard to case.

Without `--apply`, cleanup only reports candidates and leaves installed entries
unchanged. `--apply` requires `--yes`; it can now remove eligible empty-directory
observations that previously appeared only as malformed review items. Native
validation checks each directory again and refuses removal if it has become
populated. Empty directories with recorded sources/ownership, other consumer
exposures, conflicting records or incomplete discovery remain review-only.
Sharing a name alone does not protect an otherwise eligible empty directory;
removing it leaves the real skill elsewhere unchanged. Empty-directory rows
retain related findings in their reasons and use `EmptyDirectory` for kind
filtering, including when the earlier observation was a name collision.
`--yes` does not override those protections. Cleanup deletion has no automatic
undo. Review the JSON before applying, for example:

```bash
skillview --scan-root /path/to/skills cleanup --candidates=EmptyDirectory --json
skillview --scan-root /path/to/skills cleanup --candidates EmptyDirectory --apply --yes --json
```

An apply batch can remove eligible entries while refusing protected entries;
inspect the per-entry `applied` results and exit status.

Cleanup's **Ignore** action stores a preference outside the skill directory. It
suppresses suggestions for that physical installation across its consumer
contexts; agent loading and update policy stay unchanged. The CLI accepts an
exact `--path` (including an agent symlink) or `--install-id`, plus `--json`.
Put global flags before the command, for example:

```bash
skillview --scan-root /path/to/skills ignore --path /path/to/skills/demo --json
skillview --scan-root /path/to/skills ignore --path /path/to/skills/demo --clear --json
```

`--clear` clears external preferences for the selected installation. Legacy
`.skillview-ignore` markers remain unchanged and are reported separately. Known
source preferences survive revision changes but expire after source or owner
context changes. Unknown-source preferences require matching native object and
whole-payload evidence; content changes or unavailable evidence leave suggestions
visible. `list --json` reports each row's `cleanupSuppression` and independent
`preferenceDiagnostics`. Corrupt or inaccessible preference state leaves external
suppression inactive. Cleanup reports unsupported non-installation entries as
skipped. Clearing stale records for a deleted installation, repairing damaged
preference storage, and migrating legacy markers are not yet offered.

Inventory also observes Codex bundled/admin skills and versioned Codex/Claude
plugin caches. Managed rows show their distributor and package version separately
from authored source metadata. A cached plugin or installation record does not
prove that an agent has enabled or loaded it. These resources are read-only in
SkillView; use the owning agent or package manager to maintain them.

Managed discovery honors `CODEX_HOME`, `CLAUDE_CONFIG_DIR`,
`CLAUDE_CODE_PLUGIN_CACHE_DIR`, `CLAUDE_CODE_PLUGIN_DIRS`, and
`CLAUDE_CODE_PLUGIN_SEED_DIR`, retaining default homes alongside overrides.
Claude registry contexts are observed separately when their schema is supported.
External registered plugins require an explicit `--scan-root` before their
payloads are read; a root inside one plugin does not authorize sibling scans.
Malformed metadata, inaccessible locations and exceeded budgets remain visible
as diagnostics. They cannot make a protected installation writable.

Exact named installs into unrelated ordinary siblings remain available. Installing
`--all` into a root containing managed resources requires an immutable staged
maintenance workflow; a separate source listing cannot authorize gh's later
rediscovery of a mutable set. `list --json` includes `managedRegions` for these
protection boundaries independently of the roots whose skill payloads were read.
Use `doctor --inventory --json` for environment and inventory diagnostics together.
The TUI Doctor includes the latest Installed snapshot when one has been captured.

Observe another workspace or a named agent profile with global flags before
`list`, other CLI commands, or the TUI:

```sh
skillview --workspace /work/project-a --workspace /work/project-b list --json
skillview --profile claude:work=/profiles/work/skills list --json
```

Workspace discovery observes known skill directories in the selected directory
and bounded ancestors inside its worktree. It also observes gh's actual project
context when Git environment variables redirect the CLI. Profile paths name
skills directories; they preserve the profile label without changing agent
configuration or claiming that the agent has loaded the skill.

Namespace containers are reported separately from their skill payloads. List
JSON includes `discoveryLocations` for containers, hidden exclusions, unsupported
linked containers and scan limits. Add a linked container's target as an explicit
`--scan-root` to inspect it. Incomplete scans retain `partial: true`; cleanup JSON
also includes that flag and inaccessible locations. Hidden residue and name
collisions require review.

### Verify backup history

Run `skillview history --json` to verify retained backup evidence, or
`skillview history --id <backup-id> --json` for one recorded ID. Verification
checks both journals, the archive hash, every payload blob, modes, directory
relationships and complete payload fingerprint. The command performs no remote
check and does not extract or replace any installed skill.

Statuses are `verified`, `incomplete`, `orphaned`, `corrupt` and `unrecognized`.
Evidence remains on disk in every case. Exit0 means selected records verified;
exit1 means retained evidence needs review; exit20 means the requested ID was
not found. Missing or unsafe state, an active writer or an inspection quota
refusal returns exit10 without creating state. Invalid arguments return exit2.
A verified schema1 archive preserves file bytes, entry Unix modes and link bytes;
it does not yet support in-place restore or preserve ACLs, extended attributes,
root permissions or manager registries.

For local changes on a tracked GitHub installation, see [recorded restoration](recorded-restoration.md) for saved plans, retained originals and verified undo.
For a reviewed newer revision, see [saved updates](saved-updates.md).

### Switch an ownerless local source

For an ownerless local copy, Installed **T** reviews replacement from a different
existing durable ordinary local directory. **R** still reimports the same recorded
source; **K** checks the package offline. The CLI flow is:

```sh
skillview --scan-root /path/to/skills source-switch-plan --path /path/to/skills/demo --new-source /durable/new/demo --json
skillview source-switch-inspect --id <saved-id> --json
skillview source-switch-ack-source --id <saved-id> --expect <plan-fingerprint> --review <source-review-fingerprint> --yes
skillview source-switch-apply --id <saved-id> --yes --disposition replace-local-source-retain-original --expect <plan-fingerprint>
```

Review the changed paths and every consumer before approval. This is an explicit
baseline-less replacement, including installed-only extras/deletions; it creates
new active native objects and retains the exact original for undo. It does not
infer a merge base or fork ancestry. Git-managed sources, mixed/pinned tracking,
owner transitions, source/payload links and nested skills currently refuse.
A copied local source may contain old absolute local-path metadata; only that
tracking scalar is rebound to the selected durable source in the candidate.

Use `source-switch-recover` or `source-switch-undo` with `--id`, `--yes` and the
fresh inspection fingerprint in `--expect`. Before the first retain, selected
source freshness is required. After that move, recovery/undo use frozen evidence
even if both external sources disappear; undo restores the previous tracking.
A missing old source is allowed only after native placement exclusion is proven;
unknown/inaccessible or overlapping placement refuses before private staging.
Linux placement also refuses mount/bind transitions, including otherwise legitimate
sources on another mounted filesystem.
See [the detailed contract](../agent_docs/maintenance-local-source-switch.md).

Saved source-changing updates also provide a [frozen source review](saved-updates.md#frozen-source-review)
with captured diffs and a separate explicit acknowledgement. This user declaration
supplies no authenticity, compatibility or safety assurance; local `check` remains
unreviewed without a matching explicit review workflow.

## Opt-in offline evaluations

Use `evaluate-review` to inspect a frozen package, your representative prompts and
an explicit already-local offline Docker evaluator. `evaluate-run` requires the
exact review fingerprint and `--consent-run`. No routine inspection or update
starts evaluations. See [environment, protocol and limitations](isolated-evaluations.md).
