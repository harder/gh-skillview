# Package checks

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

- Offline package checks must exclude unsupported Markdown contexts from both
  reference-definition and reference-use passes. Unsupported reference contexts
  cannot justify definite missing-file findings. Check each captured link prefix
  before later `..` normalization; see `agent_docs/maintenance-static-package-check.md`.
