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
