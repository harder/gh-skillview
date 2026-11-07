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

Default to a credential-free offline fixture. Isolate HOME/state/CWD, Git and
client configuration in the **child environment**; never copy real gh credentials
or auth configuration into a fixture. Use a built executable for the correct RID,
not `dotnet run`. For example, prepare this private directory before attaching
an actual terminal emulator / PTY driver:

```bash
TASK_ROOT="$(mktemp -d /var/tmp/skillview-pty.XXXXXX)"
chmod 700 "$TASK_ROOT"
(umask 077; mkdir -p "$TASK_ROOT"/{home,state,work/skills,shim,config,cache,tmp})
cat > "$TASK_ROOT/shim/gh" <<'SH'
#!/bin/sh
case "$*" in
  --version) printf 'gh version 2.102.0 (offline fixture)\n' ;;
  'skill --help') printf 'offline fixture: gh skill\n' ;;
  'skill list --json skillName,agentHosts,path,pinned,scope,sourceURL,version'|
  "skill list --json skillName,agentHosts,path,pinned,scope,sourceURL,version --dir $TASK_SCAN_ROOT") printf '[]\n' ;;
  *) printf 'offline fixture refused gh invocation\n' >&2; exit 97 ;;
esac
SH
chmod 700 "$TASK_ROOT/shim/gh"
# Set TASK_APP to the absolute path of an already built host for this machine.
# Launch this command through the PTY driver, from the private working directory:
(
  cd "$TASK_ROOT/work" || exit 1
  env -i PATH="$TASK_ROOT/shim:/usr/bin:/bin" \
    HOME="$TASK_ROOT/home" USERPROFILE="$TASK_ROOT/home" \
    TASK_SCAN_ROOT="$TASK_ROOT/work/skills" \
    SKILLVIEW_STATE_HOME="$TASK_ROOT/state" XDG_STATE_HOME="$TASK_ROOT/state" \
    XDG_CONFIG_HOME="$TASK_ROOT/config" XDG_CACHE_HOME="$TASK_ROOT/cache" \
    GH_CONFIG_DIR="$TASK_ROOT/config/gh" \
    CODEX_HOME="$TASK_ROOT/config/codex" CLAUDE_CONFIG_DIR="$TASK_ROOT/config/claude" \
    GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_COUNT=0 \
    GIT_TERMINAL_PROMPT=0 GIT_CEILING_DIRECTORIES="$TASK_ROOT" \
    TMPDIR="$TASK_ROOT/tmp" TERM=xterm-256color COLUMNS=80 LINES=24 \
    HTTP_PROXY=http://127.0.0.1:9 HTTPS_PROXY=http://127.0.0.1:9 ALL_PROXY=http://127.0.0.1:9 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 \
    "$TASK_APP" --scan-root "$TASK_ROOT/work/skills"
)
```

The refusing shim's exact argument allowlist is fixture-specific. Inspect and bind
any additional **local list** form deliberately; an unexpected invocation is a
failure to retain, not permission to accept arbitrary gh commands. This empty
fixture proves UI behavior only, not real gh compatibility, authentication,
network operations or native mutation. Live contracts require separately
authorized isolated fixtures and their own evidence; do not infer them from this
shim. Never launch an AI client to verify package checks or reload advice.

- Start at 80×24 and 120×36 when the feature is responsive. Set the actual PTY
  window size; environment dimensions alone do not resize it.
- `/usr/bin/script` creates a PTY, not a terminal emulator. Answer Terminal.Gui's
  startup probes (size, cursor, device attributes and keyboard protocol). The
  opt-in `PtyStartupTests` fixture demonstrates the response sequence.
- Retain the owned child PID, exact command/environment roles, input timestamps,
  raw ANSI, decoded frames, source/binary hashes and native fixture baselines.
  On failure, record pretermination status and any signals actually sent; stop,
  wait and drain only owned workers within the harness bounds. Reading to EOF
  while the TUI is still alive can strand cleanup.
- Preserve journals, retained originals and unique fixture evidence. A temporary
  path is not permission to remove recovery data.

## Why `.agents/skills`

When `gh skill install` runs with `--scope project` and **no agent selected**,
current `gh` installs into `.agents/skills`.

That makes `.agents/skills` the most predictable sandbox root for PTY tests.

## Synchronization strategy

Do not rely only on visible screen text. Terminal.Gui redraws, status auto-clear,
and truncation make pure screen scraping brittle.

- Wait for the exact inventory/status or saved-plan condition needed by the
  next action; generic whole-screen absence checks are unreliable during redraws.
- Button labels can repeat in instructions and status text. Prefer the actual
  bracketed button when locating a click target.
- Check real focus before typing. A nonfocusable ancestor can leave a text field
  unfocused even when direct key-delivery tests insert characters. Exercise both
  the field and main-shell printable-shortcut routing through the PTY.
- Close the owned PTY master before the bounded process-exit wait on failure,
  and retain the transcript even when teardown times out.

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

### 1. Wait for startup inventory before judging focus

Startup initially paints Discover. When the first inventory contains skills,
SkillView can automatically open Installed, unless the user has already
interacted. The first Discover frame and its initial `ready` status do not prove
that startup inventory has finished. Wait for the environment/inventory result
and inspect the active view before sending keys.

With an empty inventory, Discover starts with query focus. From Installed, press
`1` then `/` to focus Discover. In Installed, `/` and `f` focus its own inventory
search field; they do not open Discover. That shortcut is consumed rather than inserted.
Do not interpret the remaining filter text as a lost Discover query without
checking which view had focus when the key arrived.

In the Discover query, printable keys enter text. To use single-letter global
shortcuts like `d`, `u`, `c`, or `I`, first leave the field. `Ctrl+Q` is the
unconditional quit path and works without leaving it; plain `q` quits only when
a top-level read-only view has focus.

In a baseline headless PTY, Terminal.Gui can hold a bare Esc byte and combine
it with the next key as Alt+key, even after a delay. Use Tab to leave the query
field when a scripted walkthrough needs a separate focus change. The opt-in
PTY test covers Esc followed by Ctrl+Q; SkillView accepts the resulting
Ctrl+Alt+Q as quit.

### 2. Search works even when naive detectors say it failed

The reliable sign is the log line:

```text
[search] results loaded: count=...
```

Screen text can mislead because:

- status auto-clears back to the default message
- result tables truncate repo names
- old frames remain in the PTY buffer

For Discover readiness, wait for the `Search:` field after explicitly entering
Discover. Startup may legitimately settle on Installed instead.

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

## Bounded walkthrough and verification

Start with local inventory, Help, Locations, exact finding expansion, comparison
and Back in the credential-free context above. Use actual selected-row/focus
transitions, not an assumed row index after Home or a title appearing during
paint. Wait for complete ready content with loading absent; a dialog is closed
only when its actual controls/body are absent and the underlying view is ready.
At wide widths Back can leave detail visible while returning focus to the table.
Keep bounded stage deadlines and preserve the first failing frame and input trace.

Search, install, removal and saved apply/Undo require their own explicitly
authorized fixtures. Do not add them to a read-only walkthrough to fill a media
slot. For a remote search fixture, retain the actual submitted query/results and
select the observed exact result; search order is not stable. Authentication
status and repository access are separate observations, not permission to copy
credentials or treat any failed command as success.

For any authorized mutation, retain the same complete child HOME/state/CWD,
Git/client configuration and discovery flags for post-attempt inspection. Do not
run a bare `list` under the user's real environment. Match the exact installation
ID **and** resolved path; names and path basenames can collide. Bind the selected
native identity and relevant bytes/modes/links before and after; verify untouched
peers and registry state as well as the intended effect. An empty result or a
successful exit alone does not prove the right object changed. A deletion has no
automatic Undo; quarantine and saved replacement use their separate fresh reviews.

Retain raw ANSI, actual keys, settled frames, fixture declarations and source/
binary bindings. Rendered or cropped media should link back to that provenance;
never fabricate terminal text or call an offline fixture a live upstream result.
Save logs only from the isolated fixture and report confirmed observations,
unexercised paths and unresolved failures separately. Build, live contracts,
native proofs and performance work need their own task authorization and bounds.
