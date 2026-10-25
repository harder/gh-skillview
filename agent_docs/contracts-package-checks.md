# Package checks and evaluation contracts

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

- Isolated evaluation is explicit `evaluate-review` then `evaluate-run` with an
  exact fingerprint and `--consent-run`. Routine inspection/updates never execute
  evaluators. Require the local digest-pinned offline Docker contract; no pulls,
  host credentials/config or weaker fallback. Fake process tests prove protocol,
  not actual isolation or agent quality. See `agent_docs/maintenance-isolated-evaluation.md`.

- Offline package checks must exclude unsupported Markdown contexts from both
  reference-definition and reference-use passes. Unsupported reference contexts
  cannot justify definite missing-file findings. Check each captured link prefix
  before later `..` normalization; see `agent_docs/maintenance-static-package-check.md`.
