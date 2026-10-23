# Explicit repository authoring validation

`authoring-validate --repository PATH --dry-run [--json]` explicitly selects a
local source directory and runs exactly `gh skill publish --dry-run -- PATH`,
with the absolute selected directory as child CWD. This is separate from installed
`check`, inventory, availability checks and saved updates. It does not accept
installed selectors, `--fix`, `--tag`, or arbitrary forwarded gh options. It never
launches a skill script or AI client. No automatic source edits occur.

Both gh 2.97.0 and 2.102.0 support the directory positional and `--dry-run`.
Their sources show that dry-run returns **after** GitHub advisory checks when a
recognized remote exists: security settings, tag rulesets, topics and tags may
require GET requests. API failures may be advisory or silently omitted by gh.
Dry-run is not an offline or universal zero-write contract; gh may create private
cache/config state. Choosing this explicit command authorizes that bounded gh
execution. A successful exit establishes only that gh completed this command.
It does not establish all candidates were discovered, that advisories were fetched,
source integrity, compatibility, behavioral safety or source review.

Primary sources (checked in both fixed versions):

- [2.97.0 publish](https://github.com/cli/cli/blob/v2.97.0/pkg/cmd/skills/publish/publish.go)
  and [discovery](https://github.com/cli/cli/blob/v2.97.0/internal/skills/discovery/discovery.go).
- [2.102.0 publish](https://github.com/cli/cli/blob/v2.102.0/pkg/cmd/skills/publish/publish.go)
  and [discovery](https://github.com/cli/cli/blob/v2.102.0/internal/skills/discovery/discovery.go).

The bounded candidate observation includes non-hidden immediate-child,
`skills/name`, `skills/namespace/name`, nested non-plugin `prefix/skills/...`,
and `plugins/namespace/skills/name` layouts. This is a candidate projection,
not a replacement for gh discovery. gh can silently skip invalid filesystem-safe
names; SkillView preserves raw command diagnostics instead of parsing human text
into invented per-skill passes. A root `SKILL.md` short-circuits gh discovery;
its publish path is `.` and its directory-name comparison cannot validate the
child layout. This workflow explicitly refuses that case and explains why.

Discovery is bounded to 8,192 namespace entries, 16 directory levels and 128
candidate payloads. Hidden namespace subtrees (including `.git`) are outside
candidate byte coverage. Linked namespace entries refuse admission; regular
metadata links and incomplete/access-denied discovery also refuse before gh.
Native held payload capture hashes exact raw bytes before and after execution,
with a shared 64-MiB limit and existing per-payload entry/depth bounds. Git
internals, unrelated repository files, ACLs/xattrs and cache state are not covered.
These observations are not an atomic repository snapshot and cannot exclude
transient concurrent changes. Changed candidates or bytes/modes are reported;
SkillView leaves them for explicit human review instead of repairing them.

The child runner freezes effective environment and selected CWD, normalizing
relative HOME/config/state/cache/temp values against the original captured CWD.
The parent environment is untouched. Inherited `GIT_DIR`, `GIT_WORK_TREE` or
`GIT_COMMON_DIR` redirects refuse this command; unset those explicitly to choose
the repository itself. Update notifiers, telemetry and prompts are disabled in
the child. The command lifetime is 60 seconds, with existing bounded process-tree
termination and canceled-output retention. Post-execution byte inspection has a
separate 15-second bound so caller cancellation does not hide observed mutations.
The CLI outer bound is 120 seconds. Output is bounded by ProcessRunner (normally
1 MiB per stream); truncation is explicit and makes the result partial. Terminal
output is sanitized; JSON retains escaped original diagnostics.

JSON schema 1 distinguishes `completed` (command exit zero, bounded output and
unchanged observed candidate bytes), `failed` (nonzero gh result), `partial`
(interruption, output truncation, changed candidates or incomplete verification),
`refused` (admission/environment/capture unavailable; no publish dry-run started),
and `skipped` (no candidate layout). Every refusal/partial/skip has concrete
reason and guidance. `execution` is `gh-skill-publish-dry-run` only when ProcessRunner confirms the
child started. A failed start is refused with `execution=none`, retained launch
diagnostics and no post-execution capture. An exception without a retained
process outcome reports `execution=unknown`; it cannot establish whether startup
completed.
Exit codes: completed 0; failed/partial 1; invalid arguments 2; environment refusal
or deadline 10; no candidates 20; caller cancellation 130. Pre-cancellation starts
nothing. Independent assessments remain format `gh-diagnostics-only`, agent
compatibility and payload integrity `unknown`, safety `not-assessed`, and source
`not-reviewed`. Before/after equality is not evidence of an authentic baseline.

PR71 completed the [historical verification](https://github.com/harder/gh-skillview/blob/0f7264f7eca48f6329f9a82d60fbf803e43dd5bd/agent_docs/evidence/maintenance-pr71-m19-m20-final.json),
including both native product hosts with gh 2.97.0 and 2.102.0. Other original
maintenance findings remain open. Opt-in representative-prompt evaluations
require an explicit runnable isolated environment and separate authorization for
any agent/API execution.
