# Effective agent loading: original M12

PR #61 merged at `351224b3e058166073370caafcba225bce12d7e9`. This
reviewed design starts the next implementation batch. Close the complete original
M12 change and acceptance, retaining M08/M09/M13-M21 obligations. Runtime
implementation and final acceptance remain pending.

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
