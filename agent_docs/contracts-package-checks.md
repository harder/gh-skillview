# Package checks

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

- Offline package checks must exclude unsupported Markdown contexts from both
  reference-definition and reference-use passes. Unsupported reference contexts
  cannot justify definite missing-file findings. Check each captured link prefix
  before later `..` normalization; see `agent_docs/maintenance-static-package-check.md`.

- Static agent compatibility uses only captured root metadata and package-specific
  source/version citations in `PackageCompatibilityChecker`. Gemini 0.46.0 header
  rejection is a limited rule; passing it never proves whole-agent compatibility.
  Do not read agent configuration or simulate permissions, trust or invocation.

- TUI K / Check package is explicit and checks exactly the physical copy in
  Installed's displayed snapshot. Keep only the last successful native-bound
  report in app memory. Bind to that snapshot reference, catalog generation,
  captured child context and the final PayloadSnapshot root observation; unknown
  identity or metadata-only fallback stays diagnostic-only with Refresh guidance.
  Display-cache reuse age is not fact validity while S remains displayed: time
  alone does not block K. Results belong only to that displayed inventory until
  reload/replacement. Installed/Issues activation uses GetAsync's existing
  one-minute reuse window; an aged S is replaced with S2 on tab load, invalidating
  even a just-completed check on S. Explain this lifetime in product copy. Do not
  broaden reuse or rebind facts to S2 without fresh evidence. Publication checks owned modal
  completion, close/cancellation/request epoch, app lifetime and Installed's same
  snapshot. Do not recapture inventory, probe identity again or check on rendering.
- Only ordinal category `reference`, code `reference-missing`, document `SKILL.md`
  is a package Problem. Other findings and review-state observations are Reviews;
  declaration Info/unknown meanings and incomplete/omitted coverage stay separate.
  Issues adds at most one UI-only Problem row for the exact checked copy; Enter
  opens the retained report with no repair action. Metadata never adds a duplicate
  Malformed Problem. Keep queue evidence/schema and check schema unchanged.
- Compose package evidence separately from InstalledIssueEvidence. Package
  Problems/Reviews contribute Has captured findings; any such finding or partial/
  omitted package evidence excludes No local findings observed for that copy.
  A clean package report cannot establish queue absence. Other copies remain
  package not checked for this inventory capture and keep their own captured-scan
  filter semantics.
