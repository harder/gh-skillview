# TUI PTY Testing

Use this when an AI agent needs to drive `skillview` through a real PTY instead
of only testing the CLI.

## Goal

Exercise the full-screen Terminal.Gui app, not just service methods:

1. launch the TUI in a real PTY
2. send key sequences
3. wait on reliable conditions
4. verify filesystem effects from the shell
5. capture snapshots and log confirmed bugs

## Recommended sandbox

Use a temp workspace and a temp `HOME`, but keep real GitHub auth available:

```bash
ROOT="$(mktemp -d /tmp/skillview-pty.XXXXXX)"
HOME_DIR="$ROOT/home"
WORK_DIR="$ROOT/work"
SCAN_ROOT="$WORK_DIR/.agents/skills"
mkdir -p "$HOME_DIR" "$WORK_DIR"
```

Recommended PTY env:

```bash
HOME="$HOME_DIR"
GH_CONFIG_DIR="$HOME/.config/gh"   # or a real gh config dir if needed
GH_TOKEN="$(gh auth token)"
TERM=xterm-256color
COLUMNS=140
LINES=42
DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
DOTNET_NOLOGO=1
SKILLVIEW_LOG=debug
```

Important:

- Prefer `GH_TOKEN` over relying on `gh auth status` inside a temp `HOME`.
- Use the built app binary:

```bash
src/SkillView.App/bin/Debug/net10.0/osx-arm64/skillview --scan-root "$SCAN_ROOT"
```

- Do **not** use `dotnet run` for PTY automation unless you need to rebuild.
  First-run SDK messages make synchronization much harder.

## Why `.agents/skills`

When `gh skill install` runs with `--scope project` and **no agent selected**,
current `gh` installs into `.agents/skills`.

That makes `.agents/skills` the most predictable sandbox root for PTY tests.

## Synchronization strategy

Do not rely only on visible screen text. Terminal.Gui redraws, status auto-clear,
and truncation make pure screen scraping brittle.

Use **condition-based waiting** with these sources, in order:

1. **File log conditions** for internal milestones
2. **Shell verification scripts** for install/remove side-effects
3. **Screen snapshots** for UX review and bug evidence

### Log path

On macOS:

```bash
$HOME/Library/Caches/SkillView/logs/skillview-YYYY-MM-DD.log
```

Useful conditions:

- search started: `gh skill search ...`
- search finished: `results loaded: count=`
- preview started: `loading OWNER/REPO/SKILL`
- preview returned: `PreviewAsync returned: succeeded=`

Do not wait on transient status lines like `7 result(s)` unless you capture them
immediately.

## Key PTY lessons from this repo

### 1. Startup focus is in the query field

At startup, plain letters go into the query field. If you want global shortcuts
like `d`, `u`, `c`, `I`, or `q`, first send `Esc`.

### 2. Search works even when naive detectors say it failed

The reliable sign is the log line:

```text
[search] results loaded: count=...
```

Screen text can mislead because:

- status auto-clears back to the default message
- result tables truncate repo names
- old frames remain in the PTY buffer

### 3. Modal screens need unique wait conditions

Do not wait for generic strings that also exist on the main screen.

Examples:

- bad: `Doctor`
- good: `## Environment`

### 4. `--scan-root` is a global flag

For CLI verification, put it **before** the subcommand:

```bash
skillview --scan-root "$SCAN_ROOT" list --json
```

Not:

```bash
skillview list --json --scan-root "$SCAN_ROOT"
```

### 5. Verification should key off resolved path as well as skill name

The inventory `name` may differ from the installed directory name. For example,
an installed `solveit-tools/` directory may appear in inventory as `solveit`.

For shell verification, match either:

- `skill.name`
- `basename(skill.resolvedPath)`

## Recommended walkthrough order

This order reduced interference during testing:

1. main screen
2. search
3. preview
4. install
5. doctor
6. help
7. update
8. cleanup
9. logs
10. installed
11. remove

Running search/install first is safer than opening multiple modals before the
first remote operation.

## Suggested verification scripts

### Install verification

Run after the PTY install action:

```bash
GH_TOKEN="$(gh auth token)" \
HOME="$HOME_DIR" \
skillview --scan-root "$SCAN_ROOT" list --json
```

Assert:

- expected skill exists by `name` **or** resolved-path basename
- `resolvedPath` is inside the temp sandbox

### Remove verification

Run after the PTY remove action:

```bash
GH_TOKEN="$(gh auth token)" \
HOME="$HOME_DIR" \
skillview --scan-root "$SCAN_ROOT" list --json
```

Assert:

- exit code `20` with `{"skills":[]}` is a valid "no matches left" result
- expected skill no longer exists
- scan root is empty or only contains unrelated leftovers you explicitly expect

## Known product issues discovered during PTY testing

These are current repo-specific pitfalls, not generic PTY problems:

1. **Searching for a random skill name is not enough.**
   `gh skill search` ordering may not place the chosen repo first. For random
   install tests, pick a query/result pair from the same CLI search output and
   drive the PTY to the stored row index.

2. **`gh auth status` may show unauthenticated in a temp `HOME` even when
   `GH_TOKEN` allows `gh skill search/install` to work.**
   Treat actual command success as authoritative for sandboxed PTY tests.

## Minimal future-agent workflow

1. Build once with `dotnet build`.
2. Create a temp sandbox (`HOME_DIR`, `WORK_DIR`, `SCAN_ROOT`).
3. Pick a query and row index from `gh skill search <query> --json ...`.
4. Launch the built `skillview` binary in a PTY with `SKILLVIEW_LOG=debug`.
5. Send `Esc` before global shortcuts.
6. Wait on log conditions for search/preview milestones.
7. Verify install/remove with CLI shell scripts after each mutation.
8. Save cleaned PTY snapshots for UX review.
9. Log only confirmed bugs, with the snapshot/log evidence that proved them.
