# Effective agent loading

Use contextual inventory and frozen subjects to analyze
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

Current analysis does not launch AI clients. Keep native/session evidence
unavailable or unknown with an explicit reason when it has not been observed.
A documented read-only RPC or list handler does not establish zero-write process
startup. Preserve the selected child context and report disagreement between
observed facts and the conditional local-policy view.

Provide a versioned CLI effective view with exact context flags and JSON, plus a
usable TUI view/context selector and per-install/Doctor explanations. Run captures
in owned background work, render frozen results, preserve cancellation and bounded
memory. Avoid changing persisted subject/owner/frontmatter journal hash inputs.

Regression coverage: precedence vs coexistence, namespaces/nested commands, linked aliases,
disabled/path settings, permissions/approval, manual-only invocation, incompatible
or malformed frontmatter, unknown versions and runtime flags, failed/incomplete
native providers, stale sessions, agent/profile/project isolation, CLI/TUI parity,
o reads/render I/O, no writes/no skill execution. Keep native fixtures and persisted-plan compatibility checks for changes to
these contracts.

Primary docs retrieved 2026-10-15 (re-read relevant settings/version references
before implementing individual rules):
- https://code.claude.com/docs/en/skills
- https://geminicli.com/docs/cli/skills/
- https://opencode.ai/docs/skills/
- Codex: use current official OpenAI documentation through the OpenAI Docs
  skill, then correlate it with safely inspected local client capabilities.

Context and evidence contracts:
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



Provider-specific parsing, trust, framing and precedence requirements are in
[discovery contracts](contracts-discovery.md). Native/session evidence remains
explicitly unavailable or unknown unless actually observed. Fixture version
labels do not claim AI-client conformance.

Run `tests/native/prove-effective-loading.py` against each applicable native host;
the CI workflow supplies exact gh paths and isolated fixtures. Historical
implementation checkpoints remain in Git history and are not current work orders.
