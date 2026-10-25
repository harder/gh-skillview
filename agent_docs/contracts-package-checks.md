# Package checks and evaluation contracts

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

- Authoring validation is explicit `authoring-validate --repository PATH --dry-run`,
  separate from installed `check` and updates. gh dry-run may perform advisory
  API reads and cache writes; never infer safety/source review or comprehensive
  discovery from exit zero. Preserve authored bytes and report changes, never
  invoke `--fix`. See `agent_docs/maintenance-authoring-validation.md`.

- Isolated evaluation is explicit `evaluate-review` then `evaluate-run` with an
  exact fingerprint and `--consent-run`. Routine inspection/updates never execute
  evaluators. Require the local digest-pinned offline Docker contract; no pulls,
  host credentials/config or weaker fallback. Fake process tests prove protocol,
  not actual isolation or agent quality. See `agent_docs/maintenance-isolated-evaluation.md`.

- Offline package checks must exclude unsupported Markdown contexts from both
  reference-definition and reference-use passes. Unsupported reference contexts
  cannot justify definite missing-file findings. Check each captured link prefix
  before later `..` normalization; see `agent_docs/maintenance-static-package-check.md`.

- Static agent compatibility uses only captured root metadata and package-specific
  source/version citations in `PackageCompatibilityChecker`. Gemini 0.46.0 header
  rejection is a limited rule; passing it never proves whole-agent compatibility.
  Do not read agent configuration or simulate permissions, trust or invocation.
