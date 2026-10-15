# Effective agent loading: original M12

PR #61 merged at `351224b3e058166073370caafcba225bce12d7e9`. This
reviewed design starts the next implementation batch. Close the complete original
M12 change and acceptance, retaining M08/M09/M13-M21 obligations. Implementation
is in review; final acceptance remains pending.

Use M10 contextual inventory + M11 frozen subjects. Add an analysis result for
one selected agent, profile, working project and client version, retaining every
candidate address/source/owner/exposure. Keep filesystem/configuration expectation,
safe native inventory observations and active-session state separate. Unknown or
partial evidence cannot establish absence, precedence or enablement.

Implement versioned cited policies for Claude, Codex, Gemini and OpenCode; other
clients and unsupported versions remain explicit unknown. Preserve namespaces,
nested/coexisting commands and invocation availability separately from selection.
Read bounded relevant user/project/managed configuration and skill front matter;
do not flatten all agents into project-over-user or infer active loading from
symlinks. Policy, disabled, incompatible and broken states outrank generic link
badges. Runtime flags/remote or cloud account state not observed stay unknown.

Native adapters may use only documented bounded read-only inventory commands,
with timeout/output limits and schema validation; no model sessions or skill code.
Audit startup behavior before invoking a client list command. Prefer observed
native inventory when applicable to the exact selected context; show disagreement
with the expected view rather than silently overriding facts. Capability/version
failure must remain visible and retryable.

Provide a versioned CLI effective view with exact context flags and JSON, plus a
usable TUI view/context selector and per-install/Doctor explanations. Run captures
in owned background work, render frozen results, preserve cancellation and bounded
memory. Avoid changing persisted subject/owner/frontmatter journal hash inputs.

Acceptance: precedence vs coexistence, namespaces/nested commands, linked aliases,
disabled/path settings, permissions/approval, manual-only invocation, incompatible
or malformed frontmatter, unknown versions and runtime flags, failed/incomplete
native providers, stale sessions, agent/profile/project isolation, CLI/TUI parity,
o reads/render I/O, no writes/no skill execution. Native disposable all-host
fixtures, full local/platform suites, old-native journal compatibility and separate
model/Copilot review remain final gates. Merge and acceptance evidence before next
batch.

Primary docs retrieved 2026-10-15 (re-read relevant settings/version references
before implementing individual rules):
- https://code.claude.com/docs/en/skills
- https://geminicli.com/docs/cli/skills/
- https://opencode.ai/docs/skills/
- Codex: use current official OpenAI documentation through the OpenAI Docs
  skill, then correlate it with safely inspected local client capabilities.

Independent plan review contracts to incorporate:
- Support matrix records actual tested versions, source revision/date and evidence
  strength. No documentation-date-to-all-versions or open-ended support claim.
- Loading-context keys include agent/profile/project/CWD/config fingerprint/client
  version/rule revision. Physical entry IDs cannot key contextual loading results.
  Same user/global link remains a candidate in two projects with different policy.
- Provider-specific settings merge, source selection and trust are explicit;
  existence is not applicability. Claude permission lists combine; trust can gate
  settings. Source: https://code.claude.com/docs/en/settings .
- Audit native command startup hooks/plugins, updates/downloads/log/config/state
  writes. If no safe inventory exists, report unavailable. Isolated HOME/config
  changes the observed context and cannot attest the original selected context.
- Native observation records context/version/schema/time/coverage and never erases
  omitted filesystem candidates. Omission cannot prove absence.
- Separate discoverable/manual/automatic/approval/precedence/disabled/reload/session
  dimensions instead of overloading Enabled.
- Portable lint failure differs from proven client rejection. Omitted portable
  name may be accepted by a client using its directory; compatibility prose does
  not prove platform rejection. Preserve both diagnoses.
- Fixtures must explicitly cover every contract above, including one user path
  under two project policies, trusted/untrusted merged settings, native omission,
  and portable-invalid/client-accepted metadata.


Read-only preparation refresh (2026-10-15):
- OpenAI skills/config/app-server pages retrieved again. Config reference describes
  a folder path while build-skills and skills/config/write examples use SKILL.md;
  preserve this documentation disagreement and test both identities against a
  supported native version before claiming enablement.
- Native skills/list supports per-CWD results, forceReload, errors and explicit
  extra roots. Audit app-server startup writes/hooks/auth before launching it;
  an RPC method being read-only does not establish read-only process startup.
- Local Gemini package metadata is 0.46.0; presence alone does not establish the
  current documented skills capability. No client was launched during preparation.
- Claude documentation now includes skillOverrides/settings visibility and synced
  account skills. Capture local policy as expectation; unobserved cloud, trust,
  invocation flags and active session state remain unknown.
Sources: [Build skills](https://learn.chatgpt.com/docs/build-skills),
[configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference),
[app server](https://learn.chatgpt.com/docs/app-server).

Implementation checkpoint (2026-10-15; M12 acceptance remains open):
- `LoadingConfigurationReader` uses native regular-file admission, bounded
  source-generated TOML/JSON/YAML parsing and header-only skill reads. Provider
  syntax is explicit; an arbitrary override suffix cannot change its parser.
  TOML 2.10.1's DOM merges duplicate scalar keys, so syntax/semantic validation
  with `SyntaxParser.ParseStrict` must precede DOM conversion. Do not remove
  that independent validation or enable reflection serialization for AOT.
- `LoadingSettings` retains source scope/applicability and known local-policy
  blockers separately from routine unobserved remote/session inputs. Claude
  project deny/ask and visibility remain applicable before trust; project allow
  is filtered separately, and managed-only locks cannot come from ordinary
  files. Gemini disabled-name arrays union. OpenCode permission order follows
  released 1.19.1/Remeda 2.26.0, including stable overwritten-key positions.
- `EffectiveSkillLoader` captures metadata, contextual registration facts and
  concrete plugin namespaces separately from broad protection boundaries and
  distributor IDs. Physical IDs stay independent of contextual loading keys.
  Partial discovery cannot establish a highest-tier winner; known higher copies
  can still justify shadowing. Competitor references are bounded to 16 per row,
  with explicit coverage; the complete candidate array remains available.
- `effective` JSON schema 1 and Installed **E** use the same capture service and
  frozen text renderer. The modal owns cancellation/drain and does not perform
  filesystem I/O while rendering. Native observations/session state remain
  separate; fixture version labels are never native conformance.
- `prove-effective-loading.py` is a disposable product-host proof driver. It
  checks contextual policies, effective Gemini-home roots, source-generated
  TOML, competitor identities and unchanged skill/configuration entries. This
  script does not claim actual AI-client conformance.

Still required before acceptance: final independent review closure, remaining
semantic/discovery gaps, TUI context/cancellation proof, full stable-build tests,
both native hosts and all four CI RIDs, minimum/current gh lanes, exact artifact
binding, old-native persisted-journal compatibility, and merged-PR evidence.
An interim macOS App Native AOT publish passed IL2026/IL3050/IL3053 promotion;
its disposable CLI smoke parsed TOML and kept all three fixture files unchanged.
That interim smoke is not final-head or all-platform acceptance evidence.

Resumed review checkpoint (M12 acceptance still open):
- Relevant competitors are indexed before applying the peer display bound;
  outside-context rows cannot hide a selected copy. Native disagreements use
  final definite invocation facts, including partial-discovery qualification.
- Gemini saved trust and extension manifests/install metadata/enablement are
  captured directly, including explicitly linked payload roots. Saved trust is
  conditional below live IDE trust. Invalid manifests, duplicate names, omitted
  directories, bounded patterns and unavailable security/hydration stay unknown.
- OpenCode captures bounded hidden/nested Markdown agent headers at each config
  directory, with explicit names and the legacy shallow mode phase. Permission
  strings and agent tools normalize before source merge; global tools normalize
  afterward. Typed unsupported headers cannot silently inherit default allow.
- The disposable product proof now includes linked Gemini extension discovery,
  saved trust evidence and a named OpenCode Markdown override, and CI invokes it
  for both native hosts on every AOT target. These remain product fixtures, not
  actual AI-client conformance or observations of the user's running sessions.

Copilot review follow-up (acceptance remains pending):
- All consumed conditional policy keys now retain uncertainty, including legacy
  OpenCode modes and Claude plugin enablement. Relative project/CWD inputs
  resolve once against the original runner directory. Selected Claude/Codex homes
  reach managed discovery through an isolated child runner.
- Frozen OpenCode discovery roots cover every configuration directory's singular
  and plural skill trees, external compatibility roots and final `skills.paths`.
  Exact native disable flags, hidden-entry rules, CWD-relative paths and tilde
  joining are separate provider contracts. Recursive root/nested skills use an
  opt-in bounded observation mode; ordinary inventory retains leaf semantics.
  Remote URLs, unresolved substitutions and unreadable roots remain incomplete.
- Native product fixtures now cover alternate Claude plugin homes, custom
  OpenCode config directories, root/nested configured skills and disable flags.
  CI resolves gh with Python's native executable lookup: Git Bash's `command -v`
  can return an MSYS path that native Windows Python cannot inspect.
